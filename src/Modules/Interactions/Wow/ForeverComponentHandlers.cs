#nullable enable

using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>Buttons on /forever replies. Every button is bound to the user it was shown to.</summary>
    [RequireContext(ContextType.Guild)]
    public class ForeverComponentHandlers : NinjaBotBaseModule
    {
        private readonly ForeverService _forever;

        public ForeverComponentHandlers(IServiceScopeFactory scopeFactory, ForeverService forever) : base(scopeFactory)
        {
            _forever = forever;
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

            var pages = ForeverViews.RosterPages(result.Value!, id => Context.Guild.GetUser((ulong)id)?.DisplayName);
            var page = int.TryParse(pageStr, out var p) ? System.Math.Clamp(p, 0, System.Math.Max(0, pages.Count - 1)) : 0;
            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = ForeverViews.Roster(result.Value!.Guild, pages, page);
                m.Components = ForeverViews.RosterPager(Context.User.Id, page, pages.Count) ?? new ComponentBuilder().Build();
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

            await ModifyOriginalResponseAsync(m =>
            {
                m.Embed = ForeverViews.CharacterSaved(character, verb, isMain, isShared);
                m.Components = ForeverViews.CharacterActions(Context.User.Id, character, isMain, canShare) ?? new ComponentBuilder().Build();
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
