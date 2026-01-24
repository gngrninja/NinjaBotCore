using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    public class PvPCommands : NinjaBotBaseModule
    {
        private readonly ILogger<PvPCommands> _logger;
        private readonly CharacterResolver _charResolver;
        private readonly WowApi _wowApi;
        private readonly HttpClient _httpClient;

        public PvPCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<PvPCommands> logger,
            CharacterResolver charResolver,
            WowApi wowApi,
            IHttpClientFactory httpClientFactory)
            : base(scopeFactory)
        {
            _logger = logger;
            _charResolver = charResolver;
            _wowApi = wowApi;
            _httpClient = httpClientFactory.CreateClient();
        }

        [SlashCommand("pvp", "View PvP ratings for a character")]
        public async Task ViewPvP(
            [Summary("character", "Character name (leave empty to use your main character)")]
            [Autocomplete(typeof(GuildCharAutocomplete))]
            string character = null,

            [Summary("realm", "Realm name (optional if using autocomplete)")]
            [Autocomplete(typeof(RealmAutocomplete))]
            string realm = null,

            [Summary("region", "Region (defaults to US if not specified)")]
            [Choice("US", "us")]
            [Choice("EU", "eu")]
            string region = null)
        {
            await DeferAsync(ephemeral: true);

            try
            {
                // Resolve character
                var resolution = await _charResolver.ResolveCharacterAsync(
                    character, realm, region, Context.User.Id, Context);

                if (!resolution.IsSuccess)
                {
                    var errorEmbed = new EmbedBuilder()
                        .WithTitle(resolution.ErrorTitle)
                        .WithDescription(resolution.ErrorMessage)
                        .WithColor(new Color(255, 0, 0))
                        .Build();
                    await FollowupAsync(embed: errorEmbed, ephemeral: true);
                    return;
                }

                var charInfo = resolution.Character;

                // Fetch PvP data and character media in parallel
                var pvpTask = FetchPvPDataAsync(charInfo);
                var mediaTask = FetchMediaAsync(charInfo);
                var summaryTask = FetchSummaryAsync(charInfo);

                await Task.WhenAll(pvpTask, mediaTask, summaryTask);

                var pvpSummary = await pvpTask;
                var media = await mediaTask;
                var summary = await summaryTask;

                if (pvpSummary == null)
                {
                    await FollowupAsync($"Could not fetch PvP data for **{charInfo.Name}**. The character may not have any PvP activity.", ephemeral: true);
                    return;
                }

                // Fetch bracket details if available
                var bracketDetails = await FetchBracketDetailsAsync(pvpSummary, charInfo.Region);

                // Build embed
                var embed = CharPvPView.Build(charInfo, pvpSummary, bracketDetails, summary, media);

                await FollowupAsync(embed: embed.Build());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ViewPvP command");
                await FollowupAsync("An error occurred while fetching PvP data. Please try again.", ephemeral: true);
            }
        }

        private async Task<ArmoryPvPSummary> FetchPvPDataAsync(CharacterInfo charInfo)
        {
            try
            {
                return await _wowApi.GetPvPSummaryAsync(charInfo.Name, charInfo.Realm, charInfo.Region);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch PvP data for {Name}-{Realm}", charInfo.Name, charInfo.Realm);
                return null;
            }
        }

        private async Task<ArmoryMedia> FetchMediaAsync(CharacterInfo charInfo)
        {
            try
            {
                return await _wowApi.GetArmoryMediaAsync(charInfo.Name, charInfo.Realm, charInfo.Region);
            }
            catch
            {
                return null;
            }
        }

        private async Task<ArmorySummary> FetchSummaryAsync(CharacterInfo charInfo)
        {
            try
            {
                return await _wowApi.GetArmorySummaryAsync(charInfo.Name, charInfo.Realm, charInfo.Region);
            }
            catch
            {
                return null;
            }
        }

        private async Task<List<ArmoryPvPBracket>> FetchBracketDetailsAsync(ArmoryPvPSummary summary, string region)
        {
            if (summary?.Brackets == null || summary.Brackets.Count == 0)
                return new List<ArmoryPvPBracket>();

            var results = new List<ArmoryPvPBracket>();

            foreach (var bracketLink in summary.Brackets)
            {
                if (string.IsNullOrEmpty(bracketLink.Href)) continue;

                try
                {
                    var response = await _wowApi.GetAPIRequestAsync(bracketLink.Href, true);
                    var bracket = Newtonsoft.Json.JsonConvert.DeserializeObject<ArmoryPvPBracket>(response);
                    if (bracket != null)
                        results.Add(bracket);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to fetch bracket details from {Href}", bracketLink.Href);
                }
            }

            return results;
        }
    }
}
