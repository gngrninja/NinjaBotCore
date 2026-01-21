using Discord;
using Discord.WebSocket;
using Discord.Interactions;
using Microsoft.Extensions.Logging;
using NinjaBotCore.Common;
using NinjaBotCore.Modules.Wow;
using NinjaBotCore.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>
    /// Shared character resolution logic for WoW commands.
    /// Consolidates the duplicated lookup pattern from RioCommands and ArmoryCommands.
    /// </summary>
    public class CharacterResolver
    {
        private readonly ILogger<CharacterResolver> _logger;
        private readonly WowApi _wowApi;
        private readonly WowUtilities _wowUtils;
        private readonly WowCacheService _wowCache;

        public CharacterResolver(
            ILogger<CharacterResolver> logger,
            WowApi wowApi,
            WowUtilities wowUtils,
            WowCacheService wowCache)
        {
            _logger = logger;
            _wowApi = wowApi;
            _wowUtils = wowUtils;
            _wowCache = wowCache;
        }

        /// <summary>
        /// Resolves character information from various input sources.
        /// Priority: explicit params > autocomplete format > user's main > guild lookup > armory search
        /// </summary>
        /// <param name="characterInput">Character name or autocomplete format "CharName~RealmName~Region"</param>
        /// <param name="realmInput">Explicit realm override</param>
        /// <param name="regionInput">Explicit region override</param>
        /// <param name="userId">Discord user ID for main character lookup</param>
        /// <param name="context">Interaction context for guild lookup</param>
        /// <returns>Resolved character info or error result</returns>
        public async Task<CharacterResolutionResult> ResolveCharacterAsync(
            string characterInput,
            string realmInput,
            string regionInput,
            ulong userId,
            ShardedInteractionContext context)
        {
            string charName = null;
            string realmName = realmInput;
            string regionName = regionInput;

            // Step 1: If no character specified, use user's main character
            if (string.IsNullOrEmpty(characterInput))
            {
                var charAssociation = await _wowCache.GetUserMainCharacterAsync((long)userId);

                if (charAssociation != null)
                {
                    charName = charAssociation.CharName;
                    realmName ??= charAssociation.WowRealm;
                    regionName ??= charAssociation.WowRegion;
                }
                else
                {
                    return CharacterResolutionResult.Failed(
                        "No Main Character Set",
                        "You haven't set a main character yet!\n\nUse `/getchars` to manage your saved characters.");
                }
            }
            else
            {
                // Step 2: Parse autocomplete format "CharName~RealmName~Region"
                var parts = characterInput.Split('~', 3);
                charName = parts[0];

                if (string.IsNullOrEmpty(realmName) && parts.Length >= 2)
                {
                    realmName = parts[1];
                }

                if (string.IsNullOrEmpty(regionName) && parts.Length >= 3)
                {
                    regionName = parts[2];
                }
            }

            // Step 3: If still no realm, try guild lookup
            if (string.IsNullOrEmpty(realmName))
            {
                var guildObject = await _wowUtils.GetGuildName(context);

                if (!string.IsNullOrEmpty(guildObject?.guildName))
                {
                    var effectiveRealmSlug = !string.IsNullOrEmpty(guildObject.realmSlug)
                        ? guildObject.realmSlug
                        : guildObject.realmName?.ToLower().Replace(" ", "-").Replace("'", "");

                    try
                    {
                        var guildie = await _wowApi.GetCharFromGuildAsync(
                            charName,
                            effectiveRealmSlug,
                            guildObject.guildName,
                            guildObject.regionName);

                        if (!string.IsNullOrEmpty(guildie.charName))
                        {
                            realmName = guildie.realmName;
                            regionName ??= guildie.regionName;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Guild lookup failed for {CharName}, falling back to armory search", charName);
                    }
                }
            }

            // Step 4: Still no realm? Try armory search
            if (string.IsNullOrEmpty(realmName))
            {
                try
                {
                    var chars = await _wowApi.SearchArmoryAsync(charName);
                    if (chars != null && chars.Count > 0)
                    {
                        realmName = chars[0].realmName;
                        regionName ??= "us";
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Armory search failed for {CharName}", charName);
                }
            }

            // Step 5: Final validation
            if (string.IsNullOrEmpty(realmName))
            {
                return CharacterResolutionResult.Failed(
                    "Character Not Found",
                    $"Could not find character **{charName}**.\n\nPlease specify the realm name using the `realm` parameter, or use autocomplete to select your character.");
            }

            // Default region to US
            regionName ??= "us";

            // Build realm slug
            var realmSlug = GetRealmSlug(realmName, regionName);

            return CharacterResolutionResult.Success(new CharacterInfo
            {
                Name = charName,
                Realm = realmName,
                RealmSlug = realmSlug,
                Region = regionName.ToLower(),
                Locale = GetLocaleFromRegion(regionName)
            });
        }

        /// <summary>
        /// Get realm slug for API calls based on region
        /// </summary>
        public string GetRealmSlug(string realmName, string regionName)
        {
            return regionName?.ToLower() switch
            {
                "us" => WowApi.RealmInfo?.realms?
                    .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                    .Select(s => s.slug)
                    .FirstOrDefault() ?? realmName.ToLower().Replace(" ", "-").Replace("'", ""),
                "ru" => WowApi.RealmInfoRu?.realms?
                    .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                    .Select(s => s.slug)
                    .FirstOrDefault() ?? realmName.ToLower().Replace(" ", "-").Replace("'", ""),
                "eu" => WowApi.RealmInfoEu?.realms?
                    .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                    .Select(s => s.slug)
                    .FirstOrDefault() ?? realmName.ToLower().Replace(" ", "-").Replace("'", ""),
                _ => WowApi.RealmInfo?.realms?
                    .Where(r => r.name.Replace("'", "").ToLower().Contains(realmName.ToLower()))
                    .Select(s => s.slug)
                    .FirstOrDefault() ?? realmName.ToLower().Replace(" ", "-").Replace("'", "")
            };
        }

        /// <summary>
        /// Get locale string from region
        /// </summary>
        public static string GetLocaleFromRegion(string region)
        {
            return region?.ToLower() switch
            {
                "us" => "en_US",
                "eu" => "en_GB",
                "kr" => "ko_KR",
                "tw" => "zh_TW",
                "cn" => "zh_CN",
                "ru" => "ru_RU",
                _ => "en_US"
            };
        }

        /// <summary>
        /// Get armory locale format from region (for URLs)
        /// </summary>
        public static string GetArmoryLocaleFromRegion(string region)
        {
            return region?.ToLower() switch
            {
                "us" => "en-us",
                "eu" => "en-gb",
                "ru" => "ru-ru",
                _ => "en-us"
            };
        }
    }

    /// <summary>
    /// Result of character resolution attempt
    /// </summary>
    public class CharacterResolutionResult
    {
        public bool IsSuccess { get; private set; }
        public CharacterInfo Character { get; private set; }
        public string ErrorTitle { get; private set; }
        public string ErrorMessage { get; private set; }

        public static CharacterResolutionResult Success(CharacterInfo character)
        {
            return new CharacterResolutionResult
            {
                IsSuccess = true,
                Character = character
            };
        }

        public static CharacterResolutionResult Failed(string title, string message)
        {
            return new CharacterResolutionResult
            {
                IsSuccess = false,
                ErrorTitle = title,
                ErrorMessage = message
            };
        }
    }

    /// <summary>
    /// Resolved character information
    /// </summary>
    public class CharacterInfo
    {
        public string Name { get; set; }
        public string Realm { get; set; }
        public string RealmSlug { get; set; }
        public string Region { get; set; }
        public string Locale { get; set; }

        /// <summary>
        /// Realm name URL-encoded for API calls
        /// </summary>
        public string RealmEncoded => Realm?.Replace(" ", "%20");

        /// <summary>
        /// Build WarcraftLogs character URL
        /// </summary>
        public string WarcraftLogsUrl => $"https://www.warcraftlogs.com/character/{Region}/{RealmSlug}/{Name}";

        /// <summary>
        /// Build Raider.IO character URL
        /// </summary>
        public string RaiderIoUrl => $"https://raider.io/characters/{Region}/{RealmSlug}/{Name}";

        /// <summary>
        /// Build Battle.net Armory URL
        /// </summary>
        public string ArmoryUrl
        {
            get
            {
                var locale = CharacterResolver.GetArmoryLocaleFromRegion(Region);
                return $"https://worldofwarcraft.blizzard.com/{locale}/character/{Region}/{RealmSlug}/{Name?.ToLower()}";
            }
        }
    }
}
