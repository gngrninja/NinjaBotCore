#nullable enable

using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>Buttons on /forever replies. Every button is bound to the user it was shown to.</summary>
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
                await FollowupAsync(result.Error, ephemeral: true);
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
                await FollowupAsync(result.Error, ephemeral: true);
                return;
            }
            await RefreshCharacterCardAsync(characterId);
        }

        [ComponentInteraction(ForeverViews.RosterPagePrefix + "~*~*")]
        public async Task OnRosterPage(string userIdStr, string pageStr)
        {
            if (!await GateOwner(userIdStr)) return;
            await DeferAsync();

            // Re-read on every page: shares, membership and the roster switch can all change between clicks.
            var result = await _forever.GetRosterAsync((long)Context.Guild.Id, id => Context.Guild.GetUser((ulong)id) != null);
            if (!result.Ok)
            {
                await ModifyOriginalResponseAsync(m =>
                {
                    m.Content = result.Error;
                    m.Embed = null;
                    m.Components = new ComponentBuilder().Build();
                });
                return;
            }

            var page = ForeverViews.RosterPage(result.Value!, id => Context.Guild.GetUser((ulong)id)?.DisplayName,
                int.TryParse(pageStr, out var p) ? p : 0);
            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = page.Embed;
                m.Components = ForeverViews.RosterPager(Context.User.Id, page.Page, page.PageCount) ?? new ComponentBuilder().Build();
                m.AllowedMentions = AllowedMentions.None;
            });
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

            var region = (ForeverRegion)regionValue;
            var card = await _armory.GetCardAsync(region, first, last);
            if (!card.IsFound)
            {
                await FollowupAsync(ForeverViews.CardMissing(ForeverNames.FullName(first, last), region, card.Status),
                    allowedMentions: AllowedMentions.None, ephemeral: true);
                return;
            }

            var shown = view == ForeverViews.CardGear ? ForeverViews.CardGear : ForeverViews.CardOverview;
            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = ForeverViews.Card(card.Value!, shown);
                m.Components = ForeverViews.CardButtons(Context.User.Id, card.Value!.Character, shown);
                m.AllowedMentions = AllowedMentions.None;
            });
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

            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = ForeverViews.CharacterSaved(saved, verb, isMain, isShared);
                m.Components = ForeverViews.CharacterActions(Context.User.Id, character, isMain, canShare) ?? new ComponentBuilder().Build();
                m.AllowedMentions = AllowedMentions.None;
            });
        }

        private async Task<bool> GateOwner(string userIdStr)
        {
            if (ulong.TryParse(userIdStr, out var expected) && Context.User.Id == expected) return true;
            await RespondAsync("These buttons belong to someone else.", ephemeral: true);
            return false;
        }
    }
}
