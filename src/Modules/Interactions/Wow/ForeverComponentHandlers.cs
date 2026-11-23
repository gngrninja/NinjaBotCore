#nullable enable

using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Common;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>
    /// Buttons and menus on /forever cards. Private cards are bound to the user they were shown to, and
    /// each click redraws the same message. The camp card is public: anyone may join, leave or refresh it.
    /// </summary>
    [RequireContext(ContextType.Guild)]
    public class ForeverComponentHandlers : NinjaBotBaseModule
    {
        private readonly ForeverService _forever;
        private readonly IForeverArmory _armory;
        private readonly ForeverCamps _camps;
        private readonly IForeverCatalog _catalog;

        public ForeverComponentHandlers(
            IServiceScopeFactory scopeFactory, ForeverService forever, IForeverArmory armory, ForeverCamps camps, IForeverCatalog catalog) : base(scopeFactory)
        {
            _forever = forever;
            _armory = armory;
            _camps = camps;
            _catalog = catalog;
        }

        // ===== The caller's own characters =====

        /// <summary>The card for one of the caller's characters, with what it is to them in this server.</summary>
        internal static async Task<MessageComponent> CharacterCardAsync(
            ForeverService forever, ForeverSavedCharacter saved, string? verb, long userId, long guildId, bool confirmRemove = false)
        {
            var c = saved.Character;
            var guild = await forever.GetGuildAsync(guildId);
            var isMain = await forever.GetMainIdAsync(userId, guildId) == c.Id;
            var isShared = (await forever.GetSharedIdsAsync(userId, guildId)).Contains(c.Id);
            var canShare = guild != null && ForeverService.Matches(c, guild);
            return ForeverViews.CharacterSaved(saved, verb, isMain, isShared, (ulong)userId, canShare, confirmRemove);
        }

        internal static async Task<MessageComponent> CharacterListAsync(ForeverService forever, long userId, long guildId, string? note = null)
        {
            var characters = await forever.GetCharactersAsync(userId);
            var mainId = await forever.GetMainIdAsync(userId, guildId);
            var shared = await forever.GetSharedIdsAsync(userId, guildId);
            return ForeverViews.CharacterList(characters, mainId, shared, (ulong)userId, note);
        }

        [ComponentInteraction(ForeverViews.ManagePrefix + "~*")]
        public async Task OnManage(string userIdStr, string[] selected)
        {
            if (!await GateOwner(userIdStr)) return;
            await DeferAsync();
            if (!ForeverCommands.TryParseCharacter(selected.FirstOrDefault() ?? string.Empty, out var characterId))
            {
                await ProblemAsync(GoneMessage);
                return;
            }
            await ShowCharacterAsync(characterId, verb: null);
        }

        [ComponentInteraction(ForeverViews.ListButtonPrefix + "~*")]
        public async Task OnList(string userIdStr)
        {
            if (!await GateOwner(userIdStr)) return;
            await DeferAsync();
            await Context.Interaction.ModifyToV2Async(await CharacterListAsync(_forever, (long)Context.User.Id, (long)Context.Guild.Id));
        }

        [ComponentInteraction(ForeverViews.MainButtonPrefix + "~*~*")]
        public async Task OnMakeMain(string userIdStr, string characterIdStr)
        {
            if (!await GateOwner(userIdStr) || !ForeverCommands.TryParseCharacter(characterIdStr, out var characterId)) return;
            await DeferAsync();

            var result = await _forever.SetMainAsync((long)Context.User.Id, (long)Context.Guild.Id, characterId);
            if (!result.Ok)
            {
                await ProblemAsync(result.Error);
                return;
            }
            await ShowCharacterAsync(characterId, "now your main");
        }

        [ComponentInteraction(ForeverViews.ShareButtonPrefix + "~*~*")]
        public async Task OnShare(string userIdStr, string characterIdStr)
        {
            if (!await GateOwner(userIdStr) || !ForeverCommands.TryParseCharacter(characterIdStr, out var characterId)) return;
            await DeferAsync();

            var result = await _forever.ShareAsync((long)Context.User.Id, (long)Context.Guild.Id, characterId);
            if (!result.Ok)
            {
                await ProblemAsync(result.Error);
                return;
            }
            await ShowCharacterAsync(characterId, "on the roster");
        }

        [ComponentInteraction(ForeverViews.HideButtonPrefix + "~*~*")]
        public async Task OnHide(string userIdStr, string characterIdStr)
        {
            if (!await GateOwner(userIdStr) || !ForeverCommands.TryParseCharacter(characterIdStr, out var characterId)) return;
            await DeferAsync();

            await _forever.UnshareAsync((long)Context.User.Id, (long)Context.Guild.Id, characterId);
            await ShowCharacterAsync(characterId, "off the roster");
        }

        /// <summary>The first press asks, the second ("y") deletes and goes back to the list.</summary>
        [ComponentInteraction(ForeverViews.RemoveButtonPrefix + "~*~*~*")]
        public async Task OnRemove(string userIdStr, string characterIdStr, string confirmed)
        {
            if (!await GateOwner(userIdStr) || !ForeverCommands.TryParseCharacter(characterIdStr, out var characterId)) return;
            await DeferAsync();

            if (confirmed != "y")
            {
                await ShowCharacterAsync(characterId, verb: null, confirmRemove: true);
                return;
            }

            var removed = await _forever.RemoveCharacterAsync((long)Context.User.Id, characterId);
            await Context.Interaction.ModifyToV2Async(await CharacterListAsync(_forever, (long)Context.User.Id, (long)Context.Guild.Id,
                removed == null ? null : $"🗑️ Removed **{Format.Sanitize(removed.FullName)}**."));
        }

        private const string GoneMessage = "That character is gone. Run `/forever char list` again.";

        private async Task ShowCharacterAsync(long characterId, string? verb, bool confirmRemove = false)
        {
            // Opening the card is also what brings a character up to date with the armory.
            var saved = await _forever.RefreshCharacterAsync((long)Context.User.Id, characterId);
            if (!saved.Ok)
            {
                await ProblemAsync(GoneMessage);
                return;
            }
            await Context.Interaction.ModifyToV2Async(await CharacterCardAsync(
                _forever, saved.Value!, verb, (long)Context.User.Id, (long)Context.Guild.Id, confirmRemove));
        }

        // ===== Roster =====

        [ComponentInteraction(ForeverViews.RosterPagePrefix + "~*~*")]
        public async Task OnRosterPage(string userIdStr, string pageStr)
        {
            if (!await GateOwner(userIdStr)) return;
            await DeferAsync();

            var (page, sort) = ForeverViews.ParseRosterPage(pageStr);
            await ShowRosterAsync(page, sort);
        }

        [ComponentInteraction(ForeverViews.RosterSortPrefix + "~*")]
        public async Task OnRosterSort(string userIdStr, string[] selected)
        {
            if (!await GateOwner(userIdStr)) return;
            await DeferAsync();

            await ShowRosterAsync(0, ForeverViews.ParseSort(selected.FirstOrDefault()));
        }

        [ComponentInteraction(ForeverViews.RosterPickPrefix + "~*")]
        public async Task OnRosterPick(string userIdStr, string[] selected)
        {
            if (!await GateOwner(userIdStr)) return;
            var parts = (selected.FirstOrDefault() ?? string.Empty).Split('~');
            if (parts.Length != 2
                || !ForeverNames.TryCleanPart(parts[0], "First name", out var first, out _)
                || !ForeverNames.TryCleanPart(parts[1], "Surname", out var last, out _))
            {
                await RespondAsync("That menu is out of date. Run `/forever roster` again.", ephemeral: true);
                return;
            }
            await DeferAsync();

            var guild = await _forever.GetGuildAsync((long)Context.Guild.Id);
            if (guild == null)
            {
                await ProblemAsync(ForeverService.NoGuildMessage);
                return;
            }
            await ShowCardAsync(guild.Region, first, last, ForeverViews.FromRoster + ForeverViews.CardOverview);
        }

        private async Task ShowRosterAsync(int page, ForeverRosterSort sort)
        {
            // Re-read on every page: who is on the roster and who is still a member can change between clicks.
            var result = await _forever.GetRosterAsync((long)Context.Guild.Id, id => Context.Guild.GetUser((ulong)id) != null);
            if (!result.Ok)
            {
                await Context.Interaction.ModifyToV2Async(ForeverViews.Problem(result.Error));
                return;
            }

            var shown = ForeverViews.RosterPage(result.Value!, id => Context.Guild.GetUser((ulong)id)?.DisplayName,
                page, Context.User.Id, sort, Context.Guild.IconUrl);
            await Context.Interaction.ModifyToV2Async(shown.Card);
        }

        // ===== Armory cards =====

        /// <summary>The menu on character lists sent before the list could manage characters: it opens the armory card.</summary>
        [ComponentInteraction(ForeverViews.OpenCardPrefix + "~*")]
        public async Task OnOpenCard(string userIdStr, string[] selected)
        {
            if (!await GateOwner(userIdStr)) return;
            await DeferAsync();

            var character = ForeverCommands.TryParseCharacter(selected.FirstOrDefault() ?? string.Empty, out var id)
                ? await _forever.GetCharacterAsync((long)Context.User.Id, id)
                : null;
            if (character == null)
            {
                await ProblemAsync(GoneMessage);
                return;
            }
            await ShowCardAsync(character.Region, character.FirstName, character.LastName, ForeverViews.CardOverview);
        }

        [ComponentInteraction(ForeverViews.CardButtonPrefix + "~*~*~*~*~*")]
        public async Task OnCardView(string userIdStr, string view, string regionStr, string first, string last)
        {
            if (!await GateOwner(userIdStr)) return;
            if (!TryReadCharacter(regionStr, ref first, ref last, out var region))
            {
                await RespondAsync(OutOfDate, ephemeral: true);
                return;
            }
            await DeferAsync();

            await ShowCardAsync(region, first, last, view);
        }

        /// <summary>The menu under the Professions tab: a profession's recipes, or back to the skill bars.</summary>
        [ComponentInteraction(ForeverViews.CardMenuPrefix + "~*~*~*~*~*")]
        public async Task OnCardMenu(string userIdStr, string origin, string regionStr, string first, string last, string[] selected)
        {
            if (!await GateOwner(userIdStr)) return;
            if (!TryReadCharacter(regionStr, ref first, ref last, out var region))
            {
                await RespondAsync(OutOfDate, ephemeral: true);
                return;
            }
            await DeferAsync();

            var from = origin == ForeverViews.FromRoster ? ForeverViews.FromRoster : string.Empty;
            await ShowCardAsync(region, first, last, from + (selected.FirstOrDefault() ?? ForeverViews.CardProfessions));
        }

        private const string OutOfDate = "That card is out of date. Run `/forever char view` again.";

        private static bool TryReadCharacter(string regionStr, ref string first, ref string last, out ForeverRegion region)
        {
            region = default;
            if (!int.TryParse(regionStr, out var regionValue) || !Enum.IsDefined(typeof(ForeverRegion), regionValue)
                || !ForeverNames.TryCleanPart(first, "First name", out first, out _)
                || !ForeverNames.TryCleanPart(last, "Surname", out last, out _))
                return false;
            region = (ForeverRegion)regionValue;
            return true;
        }

        private async Task ShowCardAsync(ForeverRegion region, string first, string last, string view)
        {
            var card = await _armory.GetCardAsync(region, first, last);
            if (!card.IsFound)
            {
                await ProblemAsync(ForeverViews.CardMissing(ForeverNames.FullName(first, last), region, card.Status));
                return;
            }

            var shown = (ForeverViews.IsFromRoster(view) ? ForeverViews.FromRoster : string.Empty) + ForeverViews.Tab(view);
            await Context.Interaction.ModifyToV2Async(ForeverViews.Card(card.Value!, shown, Context.User.Id));
        }

        // ===== Camp =====

        [ComponentInteraction(ForeverViews.CampButtonPrefix + "~*")]
        public async Task OnCamp(string action)
        {
            if (Context.Interaction is not IComponentInteraction { Message: { } message })
            {
                await RespondAsync("That camp card can't be read. Start a new one with `/forever camp`.", ephemeral: true);
                return;
            }
            await DeferAsync();

            var userId = Context.User.Id;
            var guildId = (long)Context.Guild.Id;
            using (await _camps.LockAsync(message.Id))
            {
                var state = _camps.Recall(message.Id, ForeverViews.Texts(message.Components));
                if (state == null)
                {
                    await ProblemAsync("This camp card is too old to update. Start a new one with `/forever camp`.");
                    return;
                }

                switch (action)
                {
                    case ForeverViews.CampJoin when state.Members.Contains(userId):
                        break;
                    case ForeverViews.CampJoin when state.Members.Count >= ForeverCampState.MaxMembers:
                        await ProblemAsync($"This camp is full at {ForeverCampState.MaxMembers}. Start another with `/forever camp`.");
                        return;
                    case ForeverViews.CampJoin:
                        if (await _camps.CamperAsync(userId, guildId) == null)
                        {
                            await ProblemAsync("Save your character first with `/forever char add`, then press **I'm in** again, so the camp knows what you can bring.");
                            return;
                        }
                        state = state.With(userId);
                        break;
                    case ForeverViews.CampLeave when !state.Members.Contains(userId):
                        await FollowupAsync(components: ForeverViews.Info("You weren't in this camp."),
                            flags: MessageFlags.ComponentsV2, allowedMentions: AllowedMentions.None, ephemeral: true);
                        return;
                    case ForeverViews.CampLeave:
                        state = state.Without(userId);
                        break;
                }

                var campers = await _camps.CampersAsync(state, guildId);
                // Upgraded camp objects are only recognised with the recipe list; without it the basic ones still count.
                var recipes = _catalog.RecipesIfLoaded
                    ?? await ForeverSuggest.WithinAsync<ForeverRecipeList>(_catalog.GetRecipesAsync()!, TimeSpan.FromSeconds(2));
                await Context.Interaction.ModifyToV2Async(ForeverViews.Camp(state, campers, ForeverCamp.Plan(campers, recipes)));
                _camps.Remember(message.Id, state);
            }
        }

        // ===== Shared =====

        /// <summary>A separate ephemeral note, so the card the button sits on stays as it was.</summary>
        private Task ProblemAsync(string? message)
            => FollowupAsync(
                components: ForeverViews.Problem(message),
                flags: MessageFlags.ComponentsV2,
                allowedMentions: AllowedMentions.None,
                ephemeral: true);

        private async Task<bool> GateOwner(string userIdStr)
        {
            if (ulong.TryParse(userIdStr, out var expected) && Context.User.Id == expected) return true;
            await RespondAsync("These buttons belong to someone else.", ephemeral: true);
            return false;
        }
    }
}
