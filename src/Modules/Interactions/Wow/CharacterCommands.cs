using Discord;
using Discord.Interactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>
    /// Character management commands for WoW character associations.
    /// Includes: /setchar, /getchars, and character management component handlers
    /// </summary>
    public class CharacterCommands : NinjaBotBaseModule
    {
        private readonly ILogger<CharacterCommands> _logger;
        private readonly WowUtilities _wowUtils;
        private readonly WowCacheService _wowCache;

        public CharacterCommands(
            IServiceScopeFactory scopeFactory,
            ILogger<CharacterCommands> logger,
            WowUtilities wowUtils,
            WowCacheService wowCache)
            : base(scopeFactory)
        {
            _logger = logger;
            _wowUtils = wowUtils;
            _wowCache = wowCache;
        }

        [SlashCommand("setchar", "Associate a WoW character with your Discord account")]
        public async Task SetMyChar(
            [Summary("character", "Character name (use autocomplete to select)")]
            [Autocomplete(typeof(GuildCharAutocomplete))]
            string character,

            [Summary("ismain", "Set this as your main character")]
            bool isMain = false)
        {
            await DeferAsync(ephemeral: true);

            string charName = null;
            string realmName = null;
            string regionName = null;
            string locale = null;

            // Handle both autocomplete format: "CharName RealmName" (space-separated)
            // and cached search format: "CharName~RealmName~Region" (tilde-separated)
            if (character.Contains('~'))
            {
                // Cached search format with tildes
                var parts = character.Split('~', 3);
                charName = parts[0];

                if (parts.Length >= 2)
                {
                    realmName = parts[1];
                }

                if (parts.Length >= 3)
                {
                    regionName = parts[2];
                }
            }
            else
            {
                // Autocomplete format with spaces
                var parts = character.Split(' ', 2);
                charName = parts[0];

                if (parts.Length > 1)
                {
                    realmName = parts[1];
                }
            }

            // If no realm from autocomplete, try to look up the character
            if (string.IsNullOrEmpty(realmName))
            {
                try
                {
                    var charResult = await _wowUtils.GetCharFromArgs(character, Context);
                    charName = charResult.charName;
                    realmName = charResult.realmName;
                    regionName = charResult.regionName;
                    locale = charResult.locale;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unable to lookup character: {Character}", character);
                    await FollowupAsync($"Unable to find character: **{character}**\n\nPlease use autocomplete to select a character, or make sure the character name is correct.", ephemeral: true);
                    return;
                }
            }
            else
            {
                // Realm provided via autocomplete or cached search, look up additional info if needed
                // Only look up region/locale if not already provided (from cached search)
                if (string.IsNullOrEmpty(regionName))
                {
                    try
                    {
                        var guildObject = await _wowUtils.GetGuildName(Context);

                        // Try to get region/locale from guild or default to US
                        if (!string.IsNullOrEmpty(guildObject.regionName))
                        {
                            regionName = guildObject.regionName;
                            locale = guildObject.locale;
                        }
                        else
                        {
                            regionName = "us";
                            locale = "en_US";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Unable to get guild info, defaulting to US region");
                        regionName = "us";
                        locale = "en_US";
                    }
                }
                else
                {
                    // Region provided from cached search, set locale based on region
                    locale = regionName.ToLower() switch
                    {
                        "us" => "en_US",
                        "eu" => "en_GB",
                        "kr" => "ko_KR",
                        "tw" => "zh_TW",
                        "cn" => "zh_CN",
                        _ => "en_US"
                    };
                }
            }

            if (string.IsNullOrEmpty(charName) || string.IsNullOrEmpty(realmName))
            {
                await FollowupAsync($"Unable to find character: **{character}**\n\nPlease use autocomplete to select a character.", ephemeral: true);
                return;
            }

            // Normalize realm name for comparison
            string normalizedRealmName = CharViewHelpers.NormalizeRealmForComparison(realmName);

            var result = await WithDbAsync(async db =>
            {
                // Check if character already exists for this user
                var existingChar = db.WowCharAssociation
                    .Where(a => a.UserId == (long)Context.User.Id)
                    .AsEnumerable() // Switch to client-side evaluation for complex string operations
                    .Where(a => a.CharName.ToLower() == charName.ToLower() &&
                                CharViewHelpers.NormalizeRealmForComparison(a.WowRealm) == normalizedRealmName)
                    .FirstOrDefault();

                if (existingChar != null)
                {
                    // Check if anything needs updating
                    if (existingChar.IsMain == isMain)
                    {
                        // No changes needed
                        return (success: true, updated: false, message: $"**{charName}** on **{realmName}** " +
                            (isMain ? "is already saved as your **main character**!" : "is already saved!") +
                            (!isMain ? "\n\nUse `/setchar` with `ismain: true` to set it as your main character." : ""));
                    }
                    else
                    {
                        // Update existing character
                        existingChar.IsMain = isMain;

                        // Backfill realm slug if missing
                        if (string.IsNullOrEmpty(existingChar.LocalRealmSlug))
                        {
                            existingChar.LocalRealmSlug = CharViewHelpers.ToRealmSlug(existingChar.WowRealm);
                        }

                        // If setting as main, unset other mains
                        if (isMain)
                        {
                            var otherMains = db.WowCharAssociation
                                .Where(a => a.UserId == (long)Context.User.Id &&
                                            a.IsMain &&
                                            a.Id != existingChar.Id)
                                .ToList();

                            foreach (var main in otherMains)
                            {
                                main.IsMain = false;
                            }
                        }

                        await db.SaveChangesAsync();

                        var mainText = isMain ? " as your **main character**" : "";
                        return (success: true, updated: true, message: $"Updated **{charName}** on **{realmName}**{mainText}!");
                    }
                }
                else
                {
                    // Add new character
                    db.WowCharAssociation.Add(new WowCharAssociation
                    {
                        UserId = (long)Context.User.Id,
                        ServerId = (long)Context.Guild.Id,
                        IsMain = isMain,
                        CharName = charName,
                        WowRealm = realmName,
                        WowRegion = regionName,
                        LocalRealmSlug = CharViewHelpers.ToRealmSlug(realmName),
                        Locale = locale
                    });

                    // If setting as main, unset other mains
                    if (isMain)
                    {
                        var otherMains = db.WowCharAssociation
                            .Where(a => a.UserId == (long)Context.User.Id && a.IsMain)
                            .ToList();

                        foreach (var main in otherMains)
                        {
                            main.IsMain = false;
                        }
                    }

                    await db.SaveChangesAsync();

                    var mainText = isMain ? " as your **main character**" : "";
                    return (success: true, updated: true, message: $"Successfully saved **{charName}** on **{realmName}**{mainText}!\n\nUse `/getchars` to see all your saved characters.");
                }
            });

            // Invalidate cache if character was updated
            if (result.updated)
            {
                _wowCache.InvalidateUserCharacters((long)Context.User.Id);
            }

            await FollowupAsync(result.message, ephemeral: true);
        }


        [SlashCommand("getchars", "List your saved WoW characters")]
        public async Task GetChars()
        {
            await DeferAsync(ephemeral: true);
            var savedChars = await _wowCache.GetUserCharactersAsync((long)Context.User.Id);
            savedChars = savedChars?
                .OrderByDescending(c => c.IsMain)
                .ThenBy(c => c.CharName)
                .ToList();

            var embed = CharacterManagementView.Build(Context.User, savedChars);
            var components = CharacterManagementView.BuildComponents(savedChars);

            await Context.Interaction.ModifyToV2Async(
                WowCardV2.FromEmbed(embed, components.Build()).Build());
        }

        /// <summary>
        /// Handle character selection from getchars menu
        /// </summary>
        [ComponentInteraction("char_select")]
        public async Task HandleCharacterSelection(string[] selections)
        {
            await DeferAsync();

            try
            {
                var characterId = long.Parse(selections[0]);
                var character = await WithDbAsync(db =>
                    db.WowCharAssociation
                        .Where(a => a.Id == characterId && a.UserId == (long)Context.User.Id)
                        .FirstOrDefaultAsync());

                if (character == null)
                {
                    await FollowupAsync("❌ Character not found.", ephemeral: true);
                    return;
                }

                var selectedCard = CharacterManagementView.BuildSelectedCard(
                    Context.User.Id,
                    character,
                    Context.User.GetAvatarUrl());
                await Context.Interaction.ModifyToV2Async(selectedCard.Build());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling character selection for user {UserId}", Context.User.Id);
                await FollowupAsync("❌ An error occurred while processing your selection.", ephemeral: true);
            }
        }

        /// <summary>
        /// Handle "Set as Main" button for character management
        /// </summary>
        [ComponentInteraction("char_set_main~*")]
        public async Task HandleSetAsMain(string characterIdStr)
        {
            await DeferAsync();

            try
            {
                var characterId = long.Parse(characterIdStr);
                var (success, message) = await WithDbAsync(async db =>
                {
                    var character = db.WowCharAssociation
                        .Where(a => a.Id == characterId && a.UserId == (long)Context.User.Id)
                        .FirstOrDefault();

                    if (character == null)
                    {
                        return (false, "❌ Character not found.");
                    }

                    if (character.IsMain)
                    {
                        return (false, $"**{character.CharName}** is already your main character!");
                    }

                    // Unset other mains
                    var otherMains = db.WowCharAssociation
                        .Where(a => a.UserId == (long)Context.User.Id && a.IsMain)
                        .ToList();

                    foreach (var main in otherMains)
                    {
                        main.IsMain = false;
                    }

                    // Set this character as main
                    character.IsMain = true;
                    await db.SaveChangesAsync();

                    return (true, $"⭐ **{character.CharName}** on **{character.WowRealm}** is now your main character!");
                });

                // Invalidate both main and all characters cache after updating IsMain flag
                _wowCache.InvalidateUserMainCharacter((long)Context.User.Id);
                _wowCache.InvalidateUserCharacters((long)Context.User.Id);

                await FollowupAsync(message, ephemeral: true);

                if (success)
                {
                    // Refresh the character list
                    await RefreshCharacterList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting character as main for user {UserId}", Context.User.Id);
                await FollowupAsync("❌ An error occurred while setting your main character.", ephemeral: true);
            }
        }

        /// <summary>
        /// Handle "Remove Character" button for character management
        /// </summary>
        [ComponentInteraction("char_remove~*")]
        public async Task HandleRemoveCharacter(string characterIdStr)
        {
            await DeferAsync();

            try
            {
                var characterId = long.Parse(characterIdStr);
                var (success, message) = await WithDbAsync(async db =>
                {
                    var character = db.WowCharAssociation
                        .Where(a => a.Id == characterId && a.UserId == (long)Context.User.Id)
                        .FirstOrDefault();

                    if (character == null)
                    {
                        return (false, "❌ Character not found.");
                    }

                    var charName = character.CharName;
                    var realmName = character.WowRealm;

                    db.WowCharAssociation.Remove(character);
                    await db.SaveChangesAsync();

                    return (true, $"🗑️ Removed **{charName}** from **{realmName}**.");
                });

                // Invalidate cache after removing character
                // Also invalidate main character cache in case removed character was main
                _wowCache.InvalidateUserCharacters((long)Context.User.Id);
                _wowCache.InvalidateUserMainCharacter((long)Context.User.Id);

                await FollowupAsync(message, ephemeral: true);

                if (success)
                {
                    // Refresh the character list
                    await RefreshCharacterList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing character for user {UserId}", Context.User.Id);
                await FollowupAsync("❌ An error occurred while removing your character.", ephemeral: true);
            }
        }

        /// <summary>
        /// Handle "Back to List" button to return to character list
        /// </summary>
        [ComponentInteraction("char_back_to_list")]
        public async Task HandleBackToList()
        {
            await DeferAsync();
            await RefreshCharacterList();
        }

        /// <summary>
        /// Helper method to refresh the character list display
        /// </summary>
        private async Task RefreshCharacterList()
        {
            try
            {
                var savedChars = await WithDbAsync(db =>
                    db.WowCharAssociation
                        .Where(c => c.UserId == (long)Context.User.Id)
                        .OrderByDescending(c => c.IsMain)
                        .ThenBy(c => c.CharName)
                        .ToListAsync());

                var embed = CharacterManagementView.Build(Context.User, savedChars);
                var components = CharacterManagementView.BuildComponents(savedChars);

                await Context.Interaction.ModifyToV2Async(
                    WowCardV2.FromEmbed(embed, components.Build()).Build());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing character list for user {UserId}", Context.User.Id);
            }
        }
    }
}
