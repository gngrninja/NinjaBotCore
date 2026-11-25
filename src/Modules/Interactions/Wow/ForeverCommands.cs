#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Common;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>
    /// /forever: a server's WoW Forever guild and roster, members' characters, armory cards, crafting,
    /// camps and the guild's logs. Everything about a character comes from Blizzard's armory; members only say which
    /// characters are theirs. Replies are Components V2 cards built by <see cref="ForeverViews"/>,
    /// shown only to the caller except the camp card, which is posted for everyone to join.
    /// </summary>
    [RequireContext(ContextType.Guild)]
    [Group("forever", "WoW Forever: characters, guild roster, crafting, camps and logs")]
    public class ForeverCommands : NinjaBotBaseModule
    {
        private readonly ForeverService _forever;
        private readonly ForeverWorkshop _workshop;
        private readonly ForeverCamps _camps;
        private readonly IForeverCatalog _catalog;
        private readonly IForeverPrices _prices;
        private readonly ForeverLogs _logs;

        public ForeverCommands(
            IServiceScopeFactory scopeFactory, ForeverService forever, ForeverWorkshop workshop, ForeverCamps camps,
            IForeverCatalog catalog, IForeverPrices prices, ForeverLogs logs) : base(scopeFactory)
        {
            _logs = logs;
            _forever = forever;
            _workshop = workshop;
            _camps = camps;
            _catalog = catalog;
            _prices = prices;
        }

        internal static bool TryParseCharacter(string value, out long id) => long.TryParse(value, out id) && id > 0;

        private Task ShowAsync(MessageComponent card) => Context.Interaction.ModifyToV2Async(card);

        private bool IsMember(long userId) => Context.Guild.GetUser((ulong)userId) != null;

        private string? DisplayName(long userId) => Context.Guild.GetUser((ulong)userId)?.DisplayName;

        [SlashCommand("help", "How to get started with WoW Forever in this server")]
        public async Task Help()
        {
            await DeferAsync(ephemeral: true);
            var userId = (long)Context.User.Id;
            var guildId = (long)Context.Guild.Id;
            var guild = await _forever.GetGuildAsync(guildId);
            var characters = await _forever.GetCharactersAsync(userId);
            var mainId = await _forever.GetMainIdAsync(userId, guildId);
            var shared = await _forever.GetSharedIdsAsync(userId, guildId);
            var isOfficer = (Context.User as IGuildUser)?.GuildPermissions.KickMembers == true;

            await ShowAsync(ForeverViews.Help(Context.Guild.Name, Context.Guild.IconUrl, Context.User.Id,
                new ForeverSetup(guild, characters.Count, characters.FirstOrDefault(c => c.Id == mainId)?.FullName, shared.Count, isOfficer)));
        }

        [SlashCommand("roster", "This server's WoW Forever guild roster, or one member's characters")]
        public async Task Roster([Summary("member", "Show just this member's characters")] IUser? member = null)
        {
            await DeferAsync(ephemeral: true);
            var result = await _forever.GetRosterAsync((long)Context.Guild.Id, IsMember);
            if (!result.Ok)
            {
                await ShowAsync(ForeverViews.Problem(result.Error));
                return;
            }
            var roster = result.Value!;

            if (member != null)
            {
                var entry = roster.Members.FirstOrDefault(m => m.UserId == (long)member.Id);
                var guildMember = Context.Guild.GetUser(member.Id);
                await ShowAsync(ForeverViews.Member(
                    guildMember?.DisplayName ?? member.Username,
                    guildMember?.GetDisplayAvatarUrl() ?? member.GetAvatarUrl(),
                    entry?.Characters ?? new List<Database.ForeverCharacter>(), entry?.MainId));
                return;
            }

            var page = ForeverViews.RosterPage(roster, DisplayName, 0, Context.User.Id, iconUrl: Context.Guild.IconUrl);
            await ShowAsync(page.Card);
        }

        [SlashCommand("craft", "What a recipe takes, what it costs and who in the guild can make it")]
        public async Task Craft(
            [Summary("recipe", "Start typing a recipe's name")][Autocomplete(typeof(ForeverRecipeAutocomplete))] string recipe)
        {
            await DeferAsync(ephemeral: true);

            var recipes = await _catalog.GetRecipesAsync();
            if (recipes == null)
            {
                await ShowAsync(ForeverViews.Problem("Blizzard didn't send Forever's recipe list. Try again in a few minutes."));
                return;
            }
            var found = ForeverWorkshop.FindRecipe(recipes, recipe);
            if (found == null)
            {
                // "0" is the value of the hint lines in the suggestions.
                await ShowAsync(ForeverViews.Problem(recipe.Trim() == "0"
                    ? "Type part of a recipe's name and pick it from the list."
                    : $"I don't know a recipe called **{Format.Sanitize(Clip(recipe))}**. Pick one from the list as you type."));
                return;
            }

            var guild = await _forever.GetGuildAsync((long)Context.Guild.Id);
            var sheet = await _workshop.SheetAsync(found, guild?.Region ?? ForeverRegion.US);
            if (guild == null)
            {
                await ShowAsync(ForeverViews.Craft(sheet, null, null));
                return;
            }

            // Reading a whole guild takes a while the first time, so the card shows how far it is.
            var who = await _workshop.CraftersAsync(guild, (long)Context.Guild.Id, IsMember, DisplayName,
                progress => ShowAsync(ForeverViews.Craft(sheet, guild, null, progress)));
            await ShowAsync(ForeverViews.Craft(sheet, guild, who));
        }

        [SlashCommand("price", "What an item goes for on Forever's auction houses")]
        public async Task Price(
            [Summary("item", "Start typing an item's name")][Autocomplete(typeof(ForeverItemAutocomplete))] string item,
            [Summary("region", "Defaults to this server's Forever guild")] ForeverRegion? region = null)
        {
            await DeferAsync(ephemeral: true);
            var shown = region ?? (await _forever.GetGuildAsync((long)Context.Guild.Id))?.Region ?? ForeverRegion.US;
            var prices = await _prices.GetAsync(shown);
            if (prices == null)
            {
                await ShowAsync(ForeverViews.Problem($"TradeSkillMaster didn't send {shown} prices. Try again in a few minutes."));
                return;
            }

            var price = ForeverWorkshop.FindItem(prices, item);
            if (price == null)
            {
                await ShowAsync(ForeverViews.Problem(item.Trim() == "0"
                    ? "Type part of an item's name and pick it from the list."
                    : $"I have no {shown} price for **{Format.Sanitize(Clip(item))}**. Pick an item from the list as you type; items that can't be auctioned have no price."));
                return;
            }
            await ShowAsync(ForeverViews.Price(price, prices));
        }

        [SlashCommand("logs", "This server's Forever guild on Warcraft Logs: its latest logs")]
        public async Task Logs()
        {
            await DeferAsync(ephemeral: true);
            var guildId = (long)Context.Guild.Id;
            var guild = await _forever.GetGuildAsync(guildId);
            if (guild == null)
            {
                await ShowAsync(ForeverViews.Problem(
                    "This server has no Forever guild yet. An officer can set one with `/forever guild set`, and its logs show up here."));
                return;
            }

            var answer = await _logs.LatestAsync(guild.Name, guild.Region);
            // The same switch retail and classic logs use: /watchlogs, in the channel they should post in.
            var watch = await WithDbAsync(async db => await db.LogMonitoring.AsNoTracking()
                .Where(l => l.ServerId == guildId)
                .Select(l => new ForeverLogWatch(l.MonitorLogs, l.ChannelId))
                .FirstOrDefaultAsync());
            await ShowAsync(ForeverViews.Logs(guild, answer, watch));
        }

        [SlashCommand("camp", "Post a camp card: people join and it works out who brings what")]
        public async Task Camp([Summary("note", "What the camp is for, like \"Fishing night at Menethil\"")] string? note = null)
        {
            // Checked before anything slow: a member without a character gets a private note, and that has to be
            // decided inside Discord's three seconds, which an armory lookup may not fit in.
            if ((await _forever.GetCharactersAsync((long)Context.User.Id)).Count == 0)
            {
                await RespondAsync(
                    components: ForeverViews.Problem("Save your character first with `/forever char add`, so the camp knows what you can bring."),
                    flags: MessageFlags.ComponentsV2, allowedMentions: AllowedMentions.None, ephemeral: true);
                return;
            }
            await DeferAsync();

            var state = new ForeverCampState(ForeverCamps.CleanTitle(note), Context.User.Id, new[] { Context.User.Id });
            var campers = await _camps.CampersAsync(state, (long)Context.Guild.Id);
            // Upgraded camp objects are only recognised with the recipe list; without it the basic ones still count.
            var recipes = _catalog.RecipesIfLoaded
                ?? await ForeverSuggest.WithinAsync<ForeverRecipeList>(_catalog.GetRecipesAsync()!, TimeSpan.FromSeconds(2));
            await ShowAsync(ForeverViews.Camp(state, campers, ForeverCamp.Plan(campers, recipes)));

            var posted = await GetOriginalResponseAsync();
            _camps.Remember(posted.Id, state);
        }

        private static string Clip(string value) => value.Length <= 60 ? value : value[..60] + "…";

        [Group("guild", "This server's WoW Forever guild")]
        public class ForeverGuildCommands : NinjaBotBaseModule
        {
            private Task ShowAsync(MessageComponent card) => Context.Interaction.ModifyToV2Async(card);

            private readonly ForeverService _forever;
            private readonly IForeverArmory _armory;

            public ForeverGuildCommands(IServiceScopeFactory scopeFactory, ForeverService forever, IForeverArmory armory) : base(scopeFactory)
            {
                _forever = forever;
                _armory = armory;
            }

            [SlashCommand("set", "Set this server's WoW Forever guild")]
            [RequireUserPermission(GuildPermission.KickMembers)]
            public async Task Set(
                [Summary("name", "Guild name, as it's spelled in game")][Autocomplete(typeof(ForeverGuildNameAutocomplete))] string name,
                [Summary("region", "Region")] ForeverRegion region)
            {
                await DeferAsync(ephemeral: true);
                var result = await _forever.SetGuildAsync((long)Context.Guild.Id, name, region, (long)Context.User.Id, Context.User.Username);
                if (!result.Ok)
                {
                    await ShowAsync(ForeverViews.Problem(result.Error));
                    return;
                }
                await ShowAsync(ForeverViews.Guild(result.Value!.Guild, Context.Guild.Name, Context.Guild.IconUrl, Context.User.Id, result.Value.Armory, justSet: true));
            }

            [SlashCommand("view", "Show this server's WoW Forever guild")]
            public async Task View()
            {
                await DeferAsync(ephemeral: true);
                var guild = await _forever.GetGuildAsync((long)Context.Guild.Id);
                if (guild == null)
                {
                    await ShowAsync(ForeverViews.NoGuild(Context.Guild.Name, Context.Guild.IconUrl));
                    return;
                }
                var armory = await _armory.GetGuildAsync(guild.Region, guild.Name);
                await ShowAsync(ForeverViews.Guild(guild, Context.Guild.Name, Context.Guild.IconUrl, Context.User.Id, armory.Value));
            }

            [SlashCommand("unset", "Remove this server's WoW Forever guild (members keep their characters)")]
            [RequireUserPermission(GuildPermission.KickMembers)]
            public async Task Unset()
            {
                await DeferAsync(ephemeral: true);
                var removed = await _forever.UnsetGuildAsync((long)Context.Guild.Id);
                await ShowAsync(removed
                    ? ForeverViews.Done("🏰 Forever guild removed. Members keep their characters.")
                    : ForeverViews.Info("This server had no Forever guild set."));
            }
        }

        [Group("char", "WoW Forever characters: look anyone up, or save your own")]
        public class ForeverCharCommands : NinjaBotBaseModule
        {
            private Task ShowAsync(MessageComponent card) => Context.Interaction.ModifyToV2Async(card);

            private readonly ForeverService _forever;
            private readonly IForeverArmory _armory;

            public ForeverCharCommands(IServiceScopeFactory scopeFactory, ForeverService forever, IForeverArmory armory) : base(scopeFactory)
            {
                _forever = forever;
                _armory = armory;
            }

            [SlashCommand("view", "Look up a WoW Forever character on Blizzard's armory")]
            public async Task View(
                [Summary("character", "One of yours, a guildmate, or anyone's first name and surname")][Autocomplete(typeof(ForeverLookupAutocomplete))] string character,
                [Summary("region", "Defaults to this server's Forever guild")] ForeverRegion? region = null)
            {
                await DeferAsync(ephemeral: true);

                string first, last;
                ForeverRegion? lookupRegion;
                var own = TryParseCharacter(character, out var id) ? await _forever.GetCharacterAsync((long)Context.User.Id, id) : null;
                if (own != null)
                {
                    (first, last, lookupRegion) = (own.FirstName, own.LastName, region ?? own.Region);
                }
                else
                {
                    if (!ForeverNames.TrySplitFullName(character, out first, out last, out var error))
                    {
                        await ShowAsync(ForeverViews.Problem($"Pick one of your characters, or look anyone up by name. {error}"));
                        return;
                    }
                    lookupRegion = region ?? (await _forever.GetGuildAsync((long)Context.Guild.Id))?.Region;
                }

                if (lookupRegion == null)
                {
                    await ShowAsync(ForeverViews.Problem("This server has no Forever guild to take the region from, so please also pick a region."));
                    return;
                }

                var card = await _armory.GetCardAsync(lookupRegion.Value, first, last);
                if (!card.IsFound)
                {
                    await ShowAsync(ForeverViews.Problem(
                        ForeverViews.CardMissing(ForeverNames.FullName(first, last), lookupRegion.Value, card.Status)));
                    return;
                }

                await ShowAsync(ForeverViews.Card(card.Value!, ForeverViews.CardOverview, Context.User.Id));
            }

            [SlashCommand("add", "Save one of your WoW Forever characters")]
            public async Task Add(
                [Summary("character", "Pick a guildmate from the list, or type a first name and surname")][Autocomplete(typeof(ForeverAddAutocomplete))] string character,
                [Summary("region", "Defaults to this server's Forever guild")] ForeverRegion? region = null)
            {
                await DeferAsync(ephemeral: true);
                if (!ForeverNames.TrySplitFullName(character, out var first, out var last, out var error))
                {
                    await ShowAsync(ForeverViews.Problem(error));
                    return;
                }

                var result = await _forever.AddCharacterAsync((long)Context.User.Id, (long)Context.Guild.Id, first, last, region);
                if (!result.Ok)
                {
                    await ShowAsync(ForeverViews.Problem(result.Error));
                    return;
                }
                await ShowAsync(await ForeverComponentHandlers.CharacterCardAsync(
                    _forever, result.Value!, "added", (long)Context.User.Id, (long)Context.Guild.Id));
            }

            [SlashCommand("list", "Your saved WoW Forever characters: set your main, show one on the roster or remove it")]
            public async Task List()
            {
                await DeferAsync(ephemeral: true);
                await ShowAsync(await ForeverComponentHandlers.CharacterListAsync(_forever, (long)Context.User.Id, (long)Context.Guild.Id));
            }
        }
    }
}
