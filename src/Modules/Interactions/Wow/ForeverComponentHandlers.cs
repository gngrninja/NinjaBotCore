#nullable enable

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
    /// Buttons and menus on /forever cards. Every one is bound to the user it was shown to, and each
    /// click redraws the same message as a Components V2 card (older embed replies are upgraded in place).
    /// </summary>
    [RequireContext(ContextType.Guild)]
    public class ForeverComponentHandlers : NinjaBotBaseModule
    {
        private readonly ForeverService _forever;
        private readonly IForeverArmory _armory;

        public ForeverComponentHandlers(IServiceScopeFactory scopeFactory, ForeverService forever, IForeverArmory armory) : base(scopeFactory)
        {
            _forever = forever;
            _armory = armory;
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
            await RefreshCharacterCardAsync(characterId);
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
            await RefreshCharacterCardAsync(characterId);
        }

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
                await RespondAsync("That menu is out of date. Run `/forever roster view` again.", ephemeral: true);
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
                await ProblemAsync("That character is gone. Run `/forever char list` again.");
                return;
            }
            await ShowCardAsync(character.Region, character.FirstName, character.LastName, ForeverViews.CardOverview);
        }

        [ComponentInteraction(ForeverViews.CardButtonPrefix + "~*~*~*~*~*")]
        public async Task OnCardView(string userIdStr, string view, string regionStr, string first, string last)
        {
            if (!await GateOwner(userIdStr)) return;
            if (!int.TryParse(regionStr, out var regionValue) || !System.Enum.IsDefined(typeof(ForeverRegion), regionValue)
                || !ForeverNames.TryCleanPart(first, "First name", out first, out _)
                || !ForeverNames.TryCleanPart(last, "Surname", out last, out _))
            {
                await RespondAsync("That button is out of date. Run `/forever char view` again.", ephemeral: true);
                return;
            }
            await DeferAsync();

            await ShowCardAsync((ForeverRegion)regionValue, first, last, view);
        }

        private async Task ShowCardAsync(ForeverRegion region, string first, string last, string view)
        {
            var card = await _armory.GetCardAsync(region, first, last);
            if (!card.IsFound)
            {
                await ProblemAsync(ForeverViews.CardMissing(ForeverNames.FullName(first, last), region, card.Status));
                return;
            }

            var shown = (ForeverViews.IsFromRoster(view) ? ForeverViews.FromRoster : string.Empty)
                + (ForeverViews.IsGear(view) ? ForeverViews.CardGear : ForeverViews.CardOverview);
            await Context.Interaction.ModifyToV2Async(ForeverViews.Card(card.Value!, shown, Context.User.Id));
        }

        private async Task ShowRosterAsync(int page, ForeverRosterSort sort)
        {
            // Re-read on every page: shares, membership and the roster switch can all change between clicks.
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

        private async Task RefreshCharacterCardAsync(long characterId)
        {
            var userId = (long)Context.User.Id;
            var guildId = (long)Context.Guild.Id;
            var character = await _forever.GetCharacterAsync(userId, characterId);
            if (character == null) return;

            var guild = await _forever.GetGuildAsync(guildId);
            var isMain = await _forever.GetMainIdAsync(userId, guildId) == characterId;
            var isShared = (await _forever.GetSharedIdsAsync(userId, guildId)).Contains(characterId);
            var canShare = !isShared && guild is { RosterEnabled: true } && ForeverService.Matches(character, guild);
            var verb = isShared ? "shared" : "saved";
            // Keeps the armory line on the card; right after an add this comes from the cache.
            var armory = await _forever.LookUpAsync(character);
            var saved = new ForeverSavedCharacter(character, armory.Value, armory.Status);

            await Context.Interaction.ModifyToV2Async(
                ForeverViews.CharacterSaved(saved, verb, isMain, isShared, Context.User.Id, canShare));
        }

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
