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
    /// /forever: WoW Forever guild + character associations, an opt-in server roster and armory lookups.
    /// All replies are ephemeral Components V2 cards built by <see cref="ForeverViews"/>.
    /// </summary>
    [RequireContext(ContextType.Guild)]
    [Group("forever", "WoW Forever guild, characters and roster")]
    public class ForeverCommands : NinjaBotBaseModule
    {
        private readonly ForeverService _forever;

        public ForeverCommands(IServiceScopeFactory scopeFactory, ForeverService forever) : base(scopeFactory)
        {
            _forever = forever;
        }

        internal static bool TryParseCharacter(string value, out long id) => long.TryParse(value, out id) && id > 0;

        internal const string PickCharacterMessage = "Pick one of your characters from the list.";

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

            await Context.Interaction.ModifyToV2Async(ForeverViews.Help(Context.Guild.Name, Context.Guild.IconUrl, Context.User.Id,
                new ForeverSetup(guild, characters.Count, characters.FirstOrDefault(c => c.Id == mainId)?.FullName, shared.Count, isOfficer)));
        }

        [Group("guild", "This server's WoW Forever guild")]
        public class ForeverGuildCommands : NinjaBotBaseModule
        {
            private Task ShowAsync(MessageComponent card) => Context.Interaction.ModifyToV2Async(card);

            private readonly ForeverService _forever;

            public ForeverGuildCommands(IServiceScopeFactory scopeFactory, ForeverService forever) : base(scopeFactory)
            {
                _forever = forever;
            }

            [SlashCommand("set", "Set this server's WoW Forever guild")]
            [RequireUserPermission(GuildPermission.KickMembers)]
            public async Task Set(
                [Summary("name", "Guild name, as it's spelled in game")][Autocomplete(typeof(ForeverGuildNameAutocomplete))] string name,
                [Summary("region", "Region")] ForeverRegion region,
                [Summary("ruleset", "Only needed if the armory doesn't have the guild")] ForeverRuleset? ruleset = null,
                [Summary("faction", "Only needed if the armory doesn't have the guild")] ForeverFaction? faction = null)
            {
                await DeferAsync(ephemeral: true);
                var result = await _forever.SetGuildAsync((long)Context.Guild.Id, name, region, ruleset, faction,
                    (long)Context.User.Id, Context.User.Username);
                if (!result.Ok)
                {
                    await ShowAsync(ForeverViews.Problem(result.Error));
                    return;
                }
                await ShowAsync(ForeverViews.Guild(result.Value!.Guild, Context.Guild.Name, Context.Guild.IconUrl, Context.User.Id, result.Value));
            }

            [SlashCommand("view", "Show this server's WoW Forever guild")]
            public async Task View()
            {
                await DeferAsync(ephemeral: true);
                var guild = await _forever.GetGuildAsync((long)Context.Guild.Id);
                await ShowAsync(guild == null
                    ? ForeverViews.NoGuild(Context.Guild.Name, Context.Guild.IconUrl)
                    : ForeverViews.Guild(guild, Context.Guild.Name, Context.Guild.IconUrl, Context.User.Id));
            }

            [SlashCommand("unset", "Remove this server's WoW Forever guild (members keep their characters)")]
            [RequireUserPermission(GuildPermission.KickMembers)]
            public async Task Unset()
            {
                await DeferAsync(ephemeral: true);
                var removed = await _forever.UnsetGuildAsync((long)Context.Guild.Id);
                await ShowAsync(removed
                    ? ForeverViews.Done("🏰 Forever guild removed and the roster is hidden. Members keep their characters.")
                    : ForeverViews.Info("This server had no Forever guild set."));
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
                    await ShowAsync(ForeverViews.Problem(result.Error));
                    return;
                }
                await ShowAsync(ForeverViews.Done(enabled
                    ? "👥 **Roster on.** Members choose what to show with `/forever roster share`; nothing is shared until they do."
                    : "👥 **Roster off.** Nobody can view it, and members' shares are kept for if you turn it back on."));
            }
        }

        [Group("char", "Your WoW Forever characters")]
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

            [SlashCommand("add", "Add a WoW Forever character (private until you share it)")]
            public async Task Add(
                [Summary(ForeverNamePartAutocomplete.FirstNameOption, "Character's first name")][Autocomplete(typeof(ForeverNamePartAutocomplete))] string firstName,
                [Summary(ForeverNamePartAutocomplete.SurnameOption, "Character's surname")][Autocomplete(typeof(ForeverNamePartAutocomplete))] string surname,
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
                    await ShowAsync(ForeverViews.Problem(result.Error));
                    return;
                }
                await RespondWithCharacterAsync(result.Value!, "added");
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

            [SlashCommand("list", "List your WoW Forever characters")]
            public async Task List()
            {
                await DeferAsync(ephemeral: true);
                var userId = (long)Context.User.Id;
                var guildId = (long)Context.Guild.Id;
                var characters = await _forever.GetCharactersAsync(userId);
                var mainId = await _forever.GetMainIdAsync(userId, guildId);
                var shared = await _forever.GetSharedIdsAsync(userId, guildId);
                await ShowAsync(ForeverViews.CharacterList(characters, mainId, shared, Context.User.Id));
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
                    await ShowAsync(ForeverViews.Problem(PickCharacterMessage));
                    return;
                }
                var result = await _forever.EditCharacterAsync((long)Context.User.Id, id,
                    new ForeverCharacterInput(firstName, surname, region, ruleset, faction, characterClass));
                if (!result.Ok)
                {
                    await ShowAsync(ForeverViews.Problem(result.Error));
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
                await ShowAsync(removed == null
                    ? ForeverViews.Problem(PickCharacterMessage)
                    : ForeverViews.Done($"🗑️ Deleted **{Format.Sanitize(removed.FullName)}**. It's no longer your main or on any server's roster."));
            }

            [SlashCommand("main", "Set your WoW Forever main for this server")]
            public async Task Main(
                [Summary("character", "Which character")][Autocomplete(typeof(ForeverCharacterAutocomplete))] string character)
            {
                await DeferAsync(ephemeral: true);
                if (!TryParseCharacter(character, out var id))
                {
                    await ShowAsync(ForeverViews.Problem(PickCharacterMessage));
                    return;
                }
                var result = await _forever.SetMainAsync((long)Context.User.Id, (long)Context.Guild.Id, id);
                await ShowAsync(result.Ok
                    ? ForeverViews.Done($"★ **{Format.Sanitize(result.Value!.FullName)}** is now your main in this server.")
                    : ForeverViews.Problem(result.Error));
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

                await ShowAsync(ForeverViews.CharacterSaved(saved, verb, isMain, isShared, Context.User.Id, canShare));
            }
        }

        [Group("roster", "This server's WoW Forever roster")]
        public class ForeverRosterCommands : NinjaBotBaseModule
        {
            private Task ShowAsync(MessageComponent card) => Context.Interaction.ModifyToV2Async(card);

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
                        entry?.Characters ?? new System.Collections.Generic.List<Database.ForeverCharacter>(), entry?.MainId));
                    return;
                }

                var page = ForeverViews.RosterPage(roster, DisplayName, 0, Context.User.Id, iconUrl: Context.Guild.IconUrl);
                await ShowAsync(page.Card);
            }

            [SlashCommand("share", "Show one of your characters on this server's roster")]
            public async Task Share(
                [Summary("character", "Which character")][Autocomplete(typeof(ForeverCharacterAutocomplete))] string character)
            {
                await DeferAsync(ephemeral: true);
                if (!TryParseCharacter(character, out var id))
                {
                    await ShowAsync(ForeverViews.Problem(PickCharacterMessage));
                    return;
                }
                var result = await _forever.ShareAsync((long)Context.User.Id, (long)Context.Guild.Id, id);
                await ShowAsync(result.Ok
                    ? ForeverViews.Done($"📋 **{Format.Sanitize(result.Value!.FullName)}** is on this server's roster. Members of this server can see its name, class and your display name. Undo with `/forever roster unshare`.")
                    : ForeverViews.Problem(result.Error));
            }

            [SlashCommand("unshare", "Take one of your characters off this server's roster")]
            public async Task Unshare(
                [Summary("character", "Which character")][Autocomplete(typeof(ForeverCharacterAutocomplete))] string character)
            {
                await DeferAsync(ephemeral: true);
                var removed = TryParseCharacter(character, out var id)
                    && await _forever.UnshareAsync((long)Context.User.Id, (long)Context.Guild.Id, id);
                await ShowAsync(removed
                    ? ForeverViews.Done("📋 Removed from this server's roster.")
                    : ForeverViews.Info("That character isn't on this server's roster."));
            }

            private bool IsMember(long userId) => Context.Guild.GetUser((ulong)userId) != null;

            private string? DisplayName(long userId) => Context.Guild.GetUser((ulong)userId)?.DisplayName;
        }
    }
}
