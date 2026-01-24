using Discord;
using NinjaBotCore.Database;
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
        /// <summary>
        /// Build the character list embed
        /// </summary>
        public static EmbedBuilder Build(IUser user, List<WowCharAssociation> characters)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            if (characters != null && characters.Any())
            {
                embed.Title = $"Your Saved Characters ({characters.Count})";
                embed.WithColor(new Color(0, 200, 150));

                foreach (var character in characters)
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

        /// <summary>
        /// Build component with character select menu and management buttons
        /// </summary>
        /// <param name="characters">List of saved characters</param>
        /// <param name="userId">User ID for button custom IDs</param>
        /// <param name="returnToCharParam">Optional: character param to return to (Name~Realm~Region)</param>
        public static ComponentBuilder BuildComponents(
            List<WowCharAssociation> characters,
            ulong? userId = null,
            string returnToCharParam = null)
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

            // Add select menu with all characters
            var selectMenuBuilder = new SelectMenuBuilder()
                .WithPlaceholder("Select a character to manage...")
                .WithCustomId("char_select")
                .WithMinValues(1)
                .WithMaxValues(1);

            foreach (var character in characters)
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

            return builder;
        }
    }
}
