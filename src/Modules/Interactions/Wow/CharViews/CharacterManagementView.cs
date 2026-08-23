using Discord;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Wow;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    /// <summary>
    /// Builds the character management view for listing and managing saved characters
    /// </summary>
    public static class CharacterManagementView
    {
        public const int CharacterPageSize = 25;
        private const int MaxComponentCharacterParamLength = 48;
        /// <summary>
        /// Build the character list embed
        /// </summary>
        public static EmbedBuilder Build(
            IUser user,
            List<WowCharAssociation> characters,
            int page = 0)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            if (characters != null && characters.Any())
            {
                var orderedCharacters = characters
                    .Select((character, index) => new { Character = character, Index = index })
                    .OrderByDescending(item => item.Character.IsMain)
                    .ThenBy(item => item.Index)
                    .Select(item => item.Character)
                    .ToList();
                var pageCount = Math.Max(1, (int)Math.Ceiling(orderedCharacters.Count / (double)CharacterPageSize));
                var currentPage = Math.Clamp(page, 0, pageCount - 1);
                embed.Title = $"Your Saved Characters ({characters.Count})";
                if (pageCount > 1)
                {
                    embed.Title += $" — Page {currentPage + 1}/{pageCount}";
                }
                embed.WithColor(new Color(0, 200, 150));

                foreach (var character in orderedCharacters
                    .Skip(currentPage * CharacterPageSize)
                    .Take(CharacterPageSize))
                {
                    var mainIndicator = character.IsMain ? "★ [MAIN]" : "";
                    var realm = !string.IsNullOrEmpty(character.WowRealm) ? character.WowRealm : "Unknown Realm";
                    var region = !string.IsNullOrEmpty(character.WowRegion) ? character.WowRegion.ToUpper() : "US";

                    sb.AppendLine($"{mainIndicator} **{character.CharName}** - {realm} ({region})");
                }

                sb.AppendLine();
                sb.AppendLine("*Select a character below to manage it*");

                embed.Description = sb.ToString();
            }
            else
            {
                embed.Title = "No Saved Characters";
                embed.WithColor(new Color(255, 165, 0));
                sb.AppendLine("You haven't saved any characters yet!");
                sb.AppendLine();
                sb.AppendLine("Use `/char` to look up a character and save it.");

                embed.Description = sb.ToString();
            }

            embed.ThumbnailUrl = user?.GetAvatarUrl();
            return embed;
        }

        public static ComponentBuilderV2 BuildSelectedCard(
            ulong userId,
            WowCharAssociation character,
            string avatarUrl = null)
        {
            if (character == null)
            {
                throw new ArgumentNullException(nameof(character));
            }

            var characterName = BoundDisplay(character.CharName, 80, "Unknown Character");
            var realm = BoundDisplay(character.WowRealm, 100, "Unknown Realm");
            var region = BoundDisplay(character.WowRegion, 12, "us").ToLowerInvariant();
            var mainIndicator = character.IsMain ? "⭐ Main character" : "Alt character";

            var controls = new ComponentBuilder()
                .WithButton(
                    label: "Set as Main",
                    customId: $"char_set_main~{character.Id}",
                    style: ButtonStyle.Success,
                    emote: new Emoji("⭐"),
                    disabled: character.IsMain)
                .WithButton(
                    label: "Remove",
                    customId: $"char_remove~{character.Id}",
                    style: ButtonStyle.Danger,
                    emote: new Emoji("🗑️"))
                .WithButton(
                    label: "View Profile",
                    customId: $"char_view_saved~{userId}~{character.Id}",
                    style: ButtonStyle.Primary,
                    emote: new Emoji("📊"))
                .WithButton(
                    label: "Back to List",
                    customId: "char_back_to_list",
                    style: ButtonStyle.Secondary,
                    emote: new Emoji("↩️"));

            var embed = new EmbedBuilder()
                .WithTitle($"📋 {characterName}")
                .WithDescription(
                    $"**{realm} ({region.ToUpperInvariant()})**\n" +
                    $"{mainIndicator}\n\nChoose an action below.")
                .WithColor(character.IsMain
                    ? new Color(255, 215, 0)
                    : new Color(0, 200, 150));

            if (!string.IsNullOrWhiteSpace(avatarUrl))
            {
                embed.WithThumbnailUrl(avatarUrl);
            }

            return WowCardV2.FromEmbed(embed, controls.Build());
        }

        public static bool IsComponentSafe(CharacterInfo character)
        {
            if (character == null)
            {
                return false;
            }

            var name = character.Name?.Trim();
            var realm = character.Realm?.Trim();
            var region = character.Region?.Trim().ToLowerInvariant();
            return !string.IsNullOrWhiteSpace(name)
                && !string.IsNullOrWhiteSpace(realm)
                && region is "us" or "eu" or "kr" or "tw" or "cn"
                && !name.Contains('~')
                && !realm.Contains('~')
                && $"{name}~{realm}~{region}".Length <= MaxComponentCharacterParamLength;
        }

        public static bool TryBuildCharacterInfo(
            WowCharAssociation character,
            out CharacterInfo charInfo)
        {
            charInfo = null;
            if (character == null)
            {
                return false;
            }

            var name = character.CharName?.Trim();
            var realm = character.WowRealm?.Trim();
            var region = character.WowRegion?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(name)
                || string.IsNullOrWhiteSpace(realm)
                || region is not ("us" or "eu" or "kr" or "tw" or "cn")
                || name.Contains('~')
                || realm.Contains('~'))
            {
                return false;
            }

            // Every downstream /char control embeds Name~Realm~Region. Keeping that
            // payload bounded ensures even the longest current retail route stays
            // below Discord's 100-character custom_id limit.
            var charParam = $"{name}~{realm}~{region}";
            if (charParam.Length > MaxComponentCharacterParamLength)
            {
                return false;
            }

            var realmSlug = character.LocalRealmSlug?.Trim();
            if (string.IsNullOrWhiteSpace(realmSlug)
                || realmSlug.Length > 64
                || realmSlug.Contains('~'))
            {
                realmSlug = CharViewHelpers.ToRealmSlug(realm);
            }

            charInfo = new CharacterInfo
            {
                Name = name,
                Realm = realm,
                RealmSlug = realmSlug,
                Region = region,
                Locale = character.Locale
            };
            return true;
        }

        private static string BoundDisplay(string value, int maxLength, string fallback)
        {
            var display = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            if (display.Length <= maxLength)
            {
                return display;
            }

            var cutAt = maxLength - 1;
            if (cutAt > 0
                && char.IsHighSurrogate(display[cutAt - 1])
                && char.IsLowSurrogate(display[cutAt]))
            {
                cutAt--;
            }

            return display.Substring(0, cutAt) + "…";
        }

        /// <summary>
        /// Build component with character select menu and management buttons
        /// </summary>
        /// <param name="characters">List of saved characters</param>
        /// <param name="userId">User ID for button custom IDs</param>
        /// <param name="returnToCharParam">Optional: character param to return to (Name~Realm~Region)</param>
        public static ComponentBuilder BuildComponents(
            List<WowCharAssociation> characters,
            ulong? userId = null,
            string returnToCharParam = null,
            int page = 0)
        {
            var builder = new ComponentBuilder();

            if (characters == null || !characters.Any())
            {
                // Even with no characters, show back button if we have a return destination
                if (!string.IsNullOrEmpty(returnToCharParam) && userId.HasValue)
                {
                    var returnName = returnToCharParam.Split('~')[0];
                    builder.WithButton(
                        label: $"Back to {returnName}",
                        customId: $"char_view_overview~{userId}~{returnToCharParam}",
                        style: ButtonStyle.Secondary,
                        emote: new Emoji("↩️"),
                        row: 0
                    );
                }
                return builder;
            }

            var selectMenuBuilder = new SelectMenuBuilder()
                .WithPlaceholder("Select a character to manage...")
                .WithCustomId("char_select")
                .WithMinValues(1)
                .WithMaxValues(1);

            var orderedCharacters = characters
                .Select((character, index) => new { Character = character, Index = index })
                .OrderByDescending(item => item.Character.IsMain)
                .ThenBy(item => item.Index)
                .Select(item => item.Character)
                .ToList();
            var pageCount = Math.Max(1, (int)Math.Ceiling(orderedCharacters.Count / (double)CharacterPageSize));
            var currentPage = Math.Clamp(page, 0, pageCount - 1);

            foreach (var character in orderedCharacters
                .Skip(currentPage * CharacterPageSize)
                .Take(CharacterPageSize))
            {
                var mainIndicator = character.IsMain ? "★ " : "";
                var realm = !string.IsNullOrEmpty(character.WowRealm) ? character.WowRealm : "Unknown Realm";
                var region = !string.IsNullOrEmpty(character.WowRegion) ? character.WowRegion.ToUpper() : "US";

                // Format: "★ CharName - RealmName (REGION)" or "CharName - RealmName (REGION)"
                var label = $"{mainIndicator}{character.CharName} - {realm} ({region})";

                // Truncate if too long
                if (label.Length > 100)
                {
                    label = label.Substring(0, 97) + "...";
                }

                // Value encodes character ID
                var value = character.Id.ToString();

                // Description shows main status
                var description = character.IsMain ? "Your main character" : "Alt character";

                selectMenuBuilder.AddOption(label, value, description);
            }

            builder.WithSelectMenu(selectMenuBuilder);

            // Add management buttons on row 1 (disabled until character is selected)
            builder.WithButton(
                label: "Set as Main",
                customId: "char_set_main",
                style: ButtonStyle.Success,
                emote: new Emoji("⭐"),
                row: 1,
                disabled: true
            );

            builder.WithButton(
                label: "Remove Character",
                customId: "char_remove",
                style: ButtonStyle.Danger,
                emote: new Emoji("🗑️"),
                row: 1,
                disabled: true
            );

            builder.WithButton(
                label: "View Profile",
                customId: "char_view_profile",
                style: ButtonStyle.Primary,
                emote: new Emoji("📊"),
                row: 1,
                disabled: true
            );

            // Add back button if we have a return destination
            if (!string.IsNullOrEmpty(returnToCharParam) && userId.HasValue)
            {
                var returnName = returnToCharParam.Split('~')[0];
                builder.WithButton(
                    label: $"Back to {returnName}",
                    customId: $"char_view_overview~{userId}~{returnToCharParam}",
                    style: ButtonStyle.Secondary,
                    emote: new Emoji("↩️"),
                    row: 1
                );
            }

            if (pageCount > 1 && userId.HasValue)
            {
                string PageCustomId(int targetPage) =>
                    string.IsNullOrEmpty(returnToCharParam)
                        ? $"char_mpage~{userId.Value}~{targetPage}"
                        : $"char_mpage_ret~{userId.Value}~{targetPage}~{returnToCharParam}";

                builder.WithButton(
                    label: "Previous",
                    customId: PageCustomId(Math.Max(0, currentPage - 1)),
                    style: ButtonStyle.Secondary,
                    emote: new Emoji("⬅️"),
                    row: 2,
                    disabled: currentPage == 0);
                builder.WithButton(
                    label: $"Page {currentPage + 1}/{pageCount}",
                    customId: $"char_manage_page_label~{userId.Value}",
                    style: ButtonStyle.Secondary,
                    row: 2,
                    disabled: true);
                builder.WithButton(
                    label: "Next",
                    customId: PageCustomId(Math.Min(pageCount - 1, currentPage + 1)),
                    style: ButtonStyle.Secondary,
                    emote: new Emoji("➡️"),
                    row: 2,
                    disabled: currentPage == pageCount - 1);
            }

            return builder;
        }
    }
}
