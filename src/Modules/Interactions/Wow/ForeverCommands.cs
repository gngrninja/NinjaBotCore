#nullable enable

using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>
    /// /forever: WoW Forever guild + character associations, an opt-in server roster and armory lookups.
    /// All replies are ephemeral.
    /// </summary>
    [RequireContext(ContextType.Guild)]
    [Group("forever", "WoW Forever guild, characters and roster")]
    public class ForeverCommands : NinjaBotBaseModule
    {
        public ForeverCommands(IServiceScopeFactory scopeFactory) : base(scopeFactory) { }

        internal static bool TryParseCharacter(string value, out long id) => long.TryParse(value, out id) && id > 0;

        internal const string PickCharacterMessage = "Pick one of your characters from the list.";

        [Group("guild", "This server's WoW Forever guild")]
        public class ForeverGuildCommands : NinjaBotBaseModule
        {
            private readonly ForeverService _forever;

            public ForeverGuildCommands(IServiceScopeFactory scopeFactory, ForeverService forever) : base(scopeFactory)
            {
                _forever = forever;
            }

            [SlashCommand("set", "Set this server's WoW Forever guild")]
            [RequireUserPermission(GuildPermission.KickMembers)]
            public async Task Set(
                [Summary("name", "Guild name, as it's spelled in game")] string name,
                [Summary("region", "Region")] ForeverRegion region,
                [Summary("ruleset", "Only needed if the armory doesn't have the guild")] ForeverRuleset? ruleset = null,
                [Summary("faction", "Only needed if the armory doesn't have the guild")] ForeverFaction? faction = null)
            {
                await DeferAsync(ephemeral: true);
                var result = await _forever.SetGuildAsync((long)Context.Guild.Id, name, region, ruleset, faction,
                    (long)Context.User.Id, Context.User.Username);
                if (!result.Ok)
                {
                    await FollowupAsync(result.Error, ephemeral: true);
                    return;
                }
                await FollowupAsync(embed: ForeverViews.Guild(result.Value!.Guild, Context.Guild.Name, result.Value), ephemeral: true);
            }

            [SlashCommand("view", "Show this server's WoW Forever guild")]
            public async Task View()
            {
                await DeferAsync(ephemeral: true);
                var guild = await _forever.GetGuildAsync((long)Context.Guild.Id);
                await FollowupAsync(
                    embed: guild == null ? ForeverViews.NoGuild(Context.Guild.Name) : ForeverViews.Guild(guild, Context.Guild.Name),
                    ephemeral: true);
            }

            [SlashCommand("unset", "Remove this server's WoW Forever guild (members keep their characters)")]
            [RequireUserPermission(GuildPermission.KickMembers)]
            public async Task Unset()
            {
                await DeferAsync(ephemeral: true);
                var removed = await _forever.UnsetGuildAsync((long)Context.Guild.Id);
                await FollowupAsync(removed
                    ? "Forever guild removed and the roster is hidden. Members keep their characters."
                    : "This server had no Forever guild set.",
                    ephemeral: true);
            }

            [SlashCommand("roster", "Turn the member roster on or off")]
            [RequireUserPermission(GuildPermission.KickMembers)]
            public async Task Roster([Summary("enabled", "Let members share characters to this server's roster")] bool enabled)
            {
                await DeferAsync(ephemeral: true);
                var result = await _forever.SetRosterEnabledAsync((long)Context.Guild.Id, enabled,
                    (long)Context.User.Id, Context.User.Username);
                if (!result.Ok)
                {
                    await FollowupAsync(result.Error, ephemeral: true);
                    return;
                }
                await FollowupAsync(enabled
                    ? "Roster on. Members choose what to show with `/forever roster share`; nothing is shared until they do."
                    : "Roster off. Nobody can view it, and members' shares are kept for if you turn it back on.",
                    ephemeral: true);
            }
        }

        [Group("char", "Your WoW Forever characters")]
        public class ForeverCharCommands : NinjaBotBaseModule
        {
            private readonly ForeverService _forever;
            private readonly IForeverArmory _armory;

            public ForeverCharCommands(IServiceScopeFactory scopeFactory, ForeverService forever, IForeverArmory armory) : base(scopeFactory)
            {
                _forever = forever;
                _armory = armory;
            }

            [SlashCommand("add", "Add a WoW Forever character (private until you share it)")]
            public async Task Add(
                [Summary("first-name", "Character's first name")] string firstName,
                [Summary("surname", "Character's surname")] string surname,
                [Summary("region", "Defaults to this server's Forever guild")] ForeverRegion? region = null,
                [Summary("class", "Only needed if the armory doesn't have the character yet")] ForeverClass? characterClass = null,
                [Summary("ruleset", "Only needed if the armory doesn't have the character yet")] ForeverRuleset? ruleset = null,
                [Summary("faction", "Only needed if the armory doesn't have the character yet")] ForeverFaction? faction = null)
            {
                await DeferAsync(ephemeral: true);
                var result = await _forever.AddCharacterAsync((long)Context.User.Id, (long)Context.Guild.Id,
                    new ForeverCharacterInput(firstName, surname, region, ruleset, faction, characterClass));
                if (!result.Ok)
                {
                    await FollowupAsync(result.Error, ephemeral: true);
                    return;
                }
                await RespondWithCharacterAsync(result.Value!, "added");
            }

            [SlashCommand("view", "Look up a WoW Forever character on Blizzard's armory")]
            public async Task View(
                [Summary("character", "One of yours, or a first name and surname")][Autocomplete(typeof(ForeverLookupAutocomplete))] string character,
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
                        await FollowupAsync($"Pick one of your characters, or look anyone up by name. {error}", ephemeral: true);
                        return;
                    }
                    lookupRegion = region ?? (await _forever.GetGuildAsync((long)Context.Guild.Id))?.Region;
                }

                if (lookupRegion == null)
                {
                    await FollowupAsync("This server has no Forever guild to take the region from, so please also pick a region.", ephemeral: true);
                    return;
                }

                var card = await _armory.GetCardAsync(lookupRegion.Value, first, last);
                if (!card.IsFound)
                {
                    await FollowupAsync(
                        ForeverViews.CardMissing(ForeverNames.FullName(first, last), lookupRegion.Value, card.Status),
                        allowedMentions: AllowedMentions.None, ephemeral: true);
                    return;
                }

                await FollowupAsync(
                    embed: ForeverViews.Card(card.Value!, ForeverViews.CardOverview),
                    components: ForeverViews.CardButtons(Context.User.Id, card.Value!.Character, ForeverViews.CardOverview),
                    allowedMentions: AllowedMentions.None,
                    ephemeral: true);
            }

            [SlashCommand("list", "List your WoW Forever characters")]
            public async Task List()
            {
                await DeferAsync(ephemeral: true);
                var userId = (long)Context.User.Id;
                var guildId = (long)Context.Guild.Id;
                var characters = await _forever.GetCharactersAsync(userId);
                var mainId = await _forever.GetMainIdAsync(userId, guildId);
                var shared = await _forever.GetSharedIdsAsync(userId, guildId);
                await FollowupAsync(embed: ForeverViews.CharacterList(characters, mainId, shared), ephemeral: true);
            }

            [SlashCommand("edit", "Edit one of your WoW Forever characters, or bring it up to date with the armory")]
            public async Task Edit(
                [Summary("character", "Which character")][Autocomplete(typeof(ForeverCharacterAutocomplete))] string character,
                [Summary("first-name", "New first name")] string? firstName = null,
                [Summary("surname", "New surname")] string? surname = null,
                [Summary("class", "Class")] ForeverClass? characterClass = null,
                [Summary("region", "Region")] ForeverRegion? region = null,
                [Summary("ruleset", "Ruleset")] ForeverRuleset? ruleset = null,
                [Summary("faction", "Faction")] ForeverFaction? faction = null)
            {
                await DeferAsync(ephemeral: true);
                if (!TryParseCharacter(character, out var id))
                {
                    await FollowupAsync(PickCharacterMessage, ephemeral: true);
                    return;
                }
                var result = await _forever.EditCharacterAsync((long)Context.User.Id, id,
                    new ForeverCharacterInput(firstName, surname, region, ruleset, faction, characterClass));
                if (!result.Ok)
                {
                    await FollowupAsync(result.Error, ephemeral: true);
                    return;
                }
                await RespondWithCharacterAsync(result.Value!, "updated");
            }

            [SlashCommand("remove", "Delete one of your WoW Forever characters everywhere")]
            public async Task Remove(
                [Summary("character", "Which character")][Autocomplete(typeof(ForeverCharacterAutocomplete))] string character)
            {
                await DeferAsync(ephemeral: true);
                var removed = TryParseCharacter(character, out var id)
                    ? await _forever.RemoveCharacterAsync((long)Context.User.Id, id)
                    : null;
                await FollowupAsync(removed == null
                    ? PickCharacterMessage
                    : $"Deleted **{removed.FullName}**. It's no longer your main or on any server's roster.",
                    ephemeral: true);
            }

            [SlashCommand("main", "Set your WoW Forever main for this server")]
            public async Task Main(
                [Summary("character", "Which character")][Autocomplete(typeof(ForeverCharacterAutocomplete))] string character)
            {
                await DeferAsync(ephemeral: true);
                if (!TryParseCharacter(character, out var id))
                {
                    await FollowupAsync(PickCharacterMessage, ephemeral: true);
                    return;
                }
                var result = await _forever.SetMainAsync((long)Context.User.Id, (long)Context.Guild.Id, id);
                await FollowupAsync(result.Ok
                    ? $"★ **{result.Value!.FullName}** is now your main in this server."
                    : result.Error,
                    ephemeral: true);
            }

            private async Task RespondWithCharacterAsync(ForeverSavedCharacter saved, string verb)
            {
                var c = saved.Character;
                var userId = (long)Context.User.Id;
                var guildId = (long)Context.Guild.Id;
                var guild = await _forever.GetGuildAsync(guildId);
                var isMain = await _forever.GetMainIdAsync(userId, guildId) == c.Id;
                var isShared = (await _forever.GetSharedIdsAsync(userId, guildId)).Contains(c.Id);
                var canShare = !isShared && guild is { RosterEnabled: true } && ForeverService.Matches(c, guild);

                await FollowupAsync(
                    embed: ForeverViews.CharacterSaved(saved, verb, isMain, isShared),
                    components: ForeverViews.CharacterActions(Context.User.Id, c, isMain, canShare),
                    allowedMentions: AllowedMentions.None,
                    ephemeral: true);
            }
        }

        [Group("roster", "This server's WoW Forever roster")]
        public class ForeverRosterCommands : NinjaBotBaseModule
        {
            private readonly ForeverService _forever;

            public ForeverRosterCommands(IServiceScopeFactory scopeFactory, ForeverService forever) : base(scopeFactory)
            {
                _forever = forever;
            }

            [SlashCommand("view", "See who plays what in WoW Forever, or one member's characters")]
            public async Task View([Summary("member", "Show just this member")] IUser? member = null)
            {
                await DeferAsync(ephemeral: true);
                var result = await _forever.GetRosterAsync((long)Context.Guild.Id, IsMember);
                if (!result.Ok)
                {
                    await FollowupAsync(result.Error, ephemeral: true);
                    return;
                }
                var roster = result.Value!;

                if (member != null)
                {
                    var entry = roster.Members.FirstOrDefault(m => m.UserId == (long)member.Id);
                    await FollowupAsync(
                        embed: ForeverViews.Member(DisplayName((long)member.Id) ?? member.Username,
                            entry?.Characters ?? new System.Collections.Generic.List<Database.ForeverCharacter>(), entry?.MainId),
                        allowedMentions: AllowedMentions.None,
                        ephemeral: true);
                    return;
                }

                var page = ForeverViews.RosterPage(roster, DisplayName, 0);
                await FollowupAsync(
                    embed: page.Embed,
                    components: ForeverViews.RosterPager(Context.User.Id, page.Page, page.PageCount),
                    allowedMentions: AllowedMentions.None,
                    ephemeral: true);
            }

            [SlashCommand("share", "Show one of your characters on this server's roster")]
            public async Task Share(
                [Summary("character", "Which character")][Autocomplete(typeof(ForeverCharacterAutocomplete))] string character)
            {
                await DeferAsync(ephemeral: true);
                if (!TryParseCharacter(character, out var id))
                {
                    await FollowupAsync(PickCharacterMessage, ephemeral: true);
                    return;
                }
                var result = await _forever.ShareAsync((long)Context.User.Id, (long)Context.Guild.Id, id);
                await FollowupAsync(result.Ok
                    ? $"📋 **{result.Value!.FullName}** is on this server's roster. Members of this server can see its name, class and your display name. Undo with `/forever roster unshare`."
                    : result.Error,
                    ephemeral: true);
            }

            [SlashCommand("unshare", "Take one of your characters off this server's roster")]
            public async Task Unshare(
                [Summary("character", "Which character")][Autocomplete(typeof(ForeverCharacterAutocomplete))] string character)
            {
                await DeferAsync(ephemeral: true);
                var removed = TryParseCharacter(character, out var id)
                    && await _forever.UnshareAsync((long)Context.User.Id, (long)Context.Guild.Id, id);
                await FollowupAsync(removed
                    ? "Removed from this server's roster."
                    : "That character isn't on this server's roster.",
                    ephemeral: true);
            }

            private bool IsMember(long userId) => Context.Guild.GetUser((ulong)userId) != null;

            private string? DisplayName(long userId) => Context.Guild.GetUser((ulong)userId)?.DisplayName;
        }
    }
}
