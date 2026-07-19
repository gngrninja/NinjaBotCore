#nullable enable

using System.Threading.Tasks;
using Discord;

namespace NinjaBotCore.Common
{
    /// <summary>
    /// Single home for the Components-V2 interaction-edit invariants: content and embeds must
    /// be cleared when (up)grading a response to V2, the flag must be set, and rendered
    /// &lt;@id&gt; mentions must never notify. Every V2 response edit goes through here.
    /// </summary>
    public static class InteractionV2Extensions
    {
        public static Task ModifyToV2Async(this IDiscordInteraction interaction, MessageComponent components)
        {
            return interaction.ModifyOriginalResponseAsync(p =>
            {
                p.Content = string.Empty;
                p.Embed = null;
                p.Components = components;
                p.Flags = MessageFlags.ComponentsV2;
                p.AllowedMentions = AllowedMentions.None;
            });
        }
    }
}
