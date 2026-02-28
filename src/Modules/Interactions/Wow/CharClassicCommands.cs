using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
using NinjaBotCore.Services;
using System;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>
    /// Classic WoW character lookup command using Classic Raider.IO API.
    /// Completely separate from retail /char to avoid breaking existing functionality.
    /// </summary>
    public class CharClassicCommands : NinjaBotBaseModule
    {
        private readonly ILogger<CharClassicCommands> _logger;
        private readonly IClassicRaiderIOApi _classicRioApi;
        private readonly WowCacheService _wowCache;

        public CharClassicCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<CharClassicCommands> logger,
            IClassicRaiderIOApi classicRioApi,
            WowCacheService wowCache)
            : base(scopeFactory)
        {
            _logger = logger;
            _classicRioApi = classicRioApi;
            _wowCache = wowCache;
        }

        [SlashCommand("charclassic", "View Classic WoW character profile from Raider.IO")]
        public async Task GetClassicCharacterProfile(
            [Summary("character", "Character name")]
            [Autocomplete(typeof(ClassicCharAutocomplete))]
            string character,

            [Summary("realm", "Classic realm name (optional if using autocomplete)")]
            [Autocomplete(typeof(ClassicRealmAutocomplete))]
            string realm = null,

            [Summary("region", "Region (defaults to US)")]
            [Choice("US", "us")]
            [Choice("EU", "eu")]
            string region = "us")
        {
            await DeferAsync(ephemeral: true);

            try
            {
                // If character came from autocomplete, it's in "Name~Realm~Region" format
                if (character.Contains('~'))
                {
                    var parts = character.Split('~', 3);
                    character = parts[0];
                    if (parts.Length > 1 && string.IsNullOrWhiteSpace(realm))
                        realm = parts[1];
                    if (parts.Length > 2)
                        region = parts[2];
                }

                if (string.IsNullOrWhiteSpace(realm))
                {
                    var errorEmbed = new EmbedBuilder()
                        .WithTitle("Realm Required")
                        .WithDescription("Please specify a realm name, or select a character from the autocomplete suggestions.")
                        .WithColor(new Color(255, 0, 0))
                        .Build();
                    await FollowupAsync(embed: errorEmbed, ephemeral: true);
                    return;
                }

                var profile = await FetchClassicProfileAsync(character, realm, region);

                if (profile == null)
                {
                    var errorEmbed = new EmbedBuilder()
                        .WithTitle("Character Not Found")
                        .WithDescription($"Could not find **{character}** on **{realm}** ({region.ToUpper()}) in Classic WoW.\n\nPlease check the character name and realm.")
                        .WithColor(new Color(255, 0, 0))
                        .Build();
                    await FollowupAsync(embed: errorEmbed, ephemeral: true);
                    return;
                }

                // Record search history for autocomplete (fire-and-forget)
                _ = _wowCache.RecordClassicSearchHistoryAsync(
                    (long)Context.User.Id,
                    profile.Name,
                    profile.Realm,
                    profile.Region);

                var embed = ClassicCharOverviewView.Build(profile);
                var components = ClassicCharOverviewView.BuildComponents(Context.User.Id, profile);

                await FollowupAsync(embed: embed.Build(), components: components.Build());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetClassicCharacterProfile command");
                await FollowupAsync("An error occurred while fetching Classic character data. Please try again.", ephemeral: true);
            }
        }

        #region Component Handlers - View Navigation

        [ComponentInteraction($"{ModalConstants.ClassicCharOverview}~*~*")]
        public async Task HandleViewOverview(string userIdStr, string charParam)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var (name, realm, region) = ParseCharParam(charParam);
            if (name == null)
            {
                await FollowupAsync("Invalid character data.", ephemeral: true);
                return;
            }

            var profile = await FetchClassicProfileAsync(name, realm, region);
            if (profile == null)
            {
                await FollowupAsync("Could not load Classic character data.", ephemeral: true);
                return;
            }

            var embed = ClassicCharOverviewView.Build(profile);
            var components = ClassicCharOverviewView.BuildDetailViewComponents(
                Context.User.Id, charParam, "overview");

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction($"{ModalConstants.ClassicCharGear}~*~*")]
        public async Task HandleViewGear(string userIdStr, string charParam)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var (name, realm, region) = ParseCharParam(charParam);
            if (name == null)
            {
                await FollowupAsync("Invalid character data.", ephemeral: true);
                return;
            }

            var profile = await FetchClassicProfileAsync(name, realm, region);
            if (profile == null)
            {
                await FollowupAsync("Could not load Classic character data.", ephemeral: true);
                return;
            }

            var embed = ClassicCharGearView.Build(profile);
            var components = ClassicCharOverviewView.BuildDetailViewComponents(
                Context.User.Id, charParam, "gear");

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction($"{ModalConstants.ClassicCharRaids}~*~*")]
        public async Task HandleViewRaids(string userIdStr, string charParam)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var (name, realm, region) = ParseCharParam(charParam);
            if (name == null)
            {
                _logger.LogWarning("Classic raids view: ParseCharParam returned null for '{CharParam}'", charParam);
                await FollowupAsync("Invalid character data.", ephemeral: true);
                return;
            }

            var profile = await FetchClassicProfileAsync(name, realm, region);
            if (profile == null)
            {
                _logger.LogWarning("Classic raids view: FetchClassicProfileAsync returned null for {Name} on {Realm}-{Region}", name, realm, region);
                await FollowupAsync("Could not load Classic character data.", ephemeral: true);
                return;
            }

            _logger.LogDebug("Classic raids view: Profile loaded for {Name}, RaidProgression has {Count} entries",
                profile.Name, profile.RaidProgression?.Count ?? 0);

            var embed = ClassicCharRaidsView.Build(profile);

            _logger.LogDebug("Classic raids view: Embed built, Description length: {Length}, Title: {Title}",
                embed.Description?.Length ?? 0, embed.Title);

            var components = ClassicCharOverviewView.BuildDetailViewComponents(
                Context.User.Id, charParam, "raids");

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        #endregion

        #region Component Handlers - Actions

        [ComponentInteraction($"{ModalConstants.ClassicCharRefresh}~*~*")]
        public async Task HandleRefresh(string userIdStr, string charParam)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var (name, realm, region) = ParseCharParam(charParam);
            if (name == null)
            {
                await FollowupAsync("Invalid character data.", ephemeral: true);
                return;
            }

            var profile = await FetchClassicProfileAsync(name, realm, region);
            if (profile == null)
            {
                await FollowupAsync("Could not load Classic character data.", ephemeral: true);
                return;
            }

            var embed = ClassicCharOverviewView.Build(profile);
            var components = ClassicCharOverviewView.BuildComponents(Context.User.Id, profile);

            await ModifyOriginalResponseAsync(msg =>
            {
                msg.Embed = embed.Build();
                msg.Components = components.Build();
            });
        }

        [ComponentInteraction($"{ModalConstants.ClassicCharShare}~*~*")]
        public async Task HandleShare(string userIdStr, string charParam)
        {
            if (!ValidateUser(userIdStr, out var errorMsg))
            {
                await RespondAsync(errorMsg, ephemeral: true);
                return;
            }

            await DeferAsync();

            var (name, realm, region) = ParseCharParam(charParam);
            if (name == null)
            {
                await FollowupAsync("Invalid character data.", ephemeral: true);
                return;
            }

            var profile = await FetchClassicProfileAsync(name, realm, region);
            if (profile == null)
            {
                await FollowupAsync("Could not load Classic character data.", ephemeral: true);
                return;
            }

            var embed = ClassicCharOverviewView.Build(profile);

            // Send as new public message (no components for shared version)
            await Context.Channel.SendMessageAsync(
                text: $"*Shared by {Context.User.Mention}*",
                embed: embed.Build());

            await FollowupAsync("Classic character profile shared!", ephemeral: true);
        }

        #endregion

        #region Helper Methods

        private async Task<ClassicRaiderIOModels.ClassicCharProfile> FetchClassicProfileAsync(
            string charName, string realm, string region)
        {
            try
            {
                return await _classicRioApi.GetCharacterProfileAsync(charName, realm, region);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch Classic RIO data for {Character} on {Realm}-{Region}", charName, realm, region);
                return null;
            }
        }

        private bool ValidateUser(string userIdStr, out string errorMessage)
        {
            errorMessage = null;

            if (!ulong.TryParse(userIdStr, out var originalUserId))
            {
                errorMessage = "Invalid interaction data.";
                return false;
            }

            if (Context.User.Id != originalUserId)
            {
                errorMessage = "This interaction belongs to another user.";
                return false;
            }

            return true;
        }

        private (string Name, string Realm, string Region) ParseCharParam(string charParam)
        {
            var parts = charParam.Split('~', 3);
            if (parts.Length < 3) return (null, null, null);

            return (parts[0], parts[1], parts[2]);
        }

        #endregion
    }
}
