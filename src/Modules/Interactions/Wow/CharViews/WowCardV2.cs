using Discord;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    /// <summary>
    /// Adapts the existing WoW view models into polished Discord Components V2 cards.
    /// Legacy component rows are copied into the V2 container so custom IDs and routing
    /// stay stable while the rendered message contains no embed or top-level content.
    /// </summary>
    public static class WowCardV2
    {
        private const int TextDisplaySoftLimit = 3900;
        private static readonly Color DefaultAccent = new(88, 101, 242);

        public static ComponentBuilderV2 FromEmbed(
            EmbedBuilder embedBuilder,
            MessageComponent controls = null,
            string preface = null)
        {
            if (embedBuilder == null)
            {
                throw new ArgumentNullException(nameof(embedBuilder));
            }

            return FromEmbed(embedBuilder.Build(), controls, preface);
        }

        public static ComponentBuilderV2 FromEmbed(
            Embed embed,
            MessageComponent controls = null,
            string preface = null)
        {
            var container = new ContainerBuilder()
                .WithAccentColor(embed.Color ?? DefaultAccent);

            AddHeader(container, embed.Title, embed.Thumbnail?.Url);
            AddDivider(container);

            if (!string.IsNullOrWhiteSpace(preface))
            {
                AddText(container, preface);
                AddDivider(container, isDivider: false);
            }

            AddText(container, embed.Description);
            AddFields(container, embed.Fields);

            var imageUrl = embed.Image?.Url;
            if (IsHttpMedia(imageUrl))
            {
                container.AddComponent(new MediaGalleryBuilder()
                    .AddItem(imageUrl, embed.Title));
            }

            var footerText = embed.Footer?.Text;
            if (!string.IsNullOrWhiteSpace(footerText))
            {
                AddDivider(container);
                AddText(container, $"-# {footerText}");
            }

            AddControls(container, controls);

            return new ComponentBuilderV2().AddComponent(container);
        }

        public static ComponentBuilderV2 Notice(
            string title,
            string message,
            Color accent,
            string emoji = null,
            MessageComponent controls = null)
        {
            var heading = string.IsNullOrWhiteSpace(emoji)
                ? title
                : $"{emoji} {title}";
            var embed = new EmbedBuilder()
                .WithTitle(heading)
                .WithDescription(message)
                .WithColor(accent);
            return FromEmbed(embed, controls);
        }

        private static void AddHeader(ContainerBuilder container, string title, string thumbnailUrl)
        {
            var heading = $"# {SanitizeHeading(string.IsNullOrWhiteSpace(title) ? "NinjaBot" : title)}";
            if (IsHttpMedia(thumbnailUrl))
            {
                container.AddComponent(new SectionBuilder(
                    new ThumbnailBuilder(new UnfurledMediaItemProperties(thumbnailUrl), title),
                    new TextDisplayBuilder().WithContent(heading)));
                return;
            }

            container.AddComponent(new TextDisplayBuilder().WithContent(heading));
        }

        private static void AddFields(ContainerBuilder container, IReadOnlyCollection<EmbedField> fields)
        {
            if (fields == null || fields.Count == 0)
            {
                return;
            }

            AddDivider(container, isDivider: false);
            var lines = new List<string>();
            foreach (var field in fields)
            {
                var value = field.Value?.ToString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                lines.Add(field.Inline
                    ? $"**{field.Name}:** {value}"
                    : $"**{field.Name}**\n{value}");
            }

            AddText(container, string.Join("\n", lines));
        }

        private static void AddControls(ContainerBuilder container, MessageComponent controls)
        {
            if (controls?.Components == null)
            {
                return;
            }

            foreach (var row in controls.Components.OfType<ActionRowComponent>())
            {
                container.AddComponent(new ActionRowBuilder(row));
            }
        }

        private static void AddText(ContainerBuilder container, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            foreach (var chunk in SplitText(content, TextDisplaySoftLimit))
            {
                container.AddComponent(new TextDisplayBuilder().WithContent(chunk));
            }
        }

        private static IEnumerable<string> SplitText(string text, int maxLength)
        {
            var remaining = text;
            var preferredBreaks = new[] { '\n', '\r', ' ' };
            while (remaining.Length > maxLength)
            {
                var splitAt = remaining.LastIndexOfAny(preferredBreaks, maxLength - 1, maxLength);
                if (splitAt < maxLength / 2)
                {
                    splitAt = maxLength;
                }
                else
                {
                    // Keep the separator in one of the chunks so conversion is lossless.
                    splitAt++;
                }

                // Never split a UTF-16 surrogate pair. A malformed pair can otherwise
                // fail JSON serialization before Discord receives the response.
                if (splitAt < remaining.Length
                    && splitAt > 0
                    && char.IsHighSurrogate(remaining[splitAt - 1])
                    && char.IsLowSurrogate(remaining[splitAt]))
                {
                    splitAt--;
                }

                yield return remaining.Substring(0, splitAt);
                remaining = remaining.Substring(splitAt);
            }

            if (remaining.Length > 0)
            {
                yield return remaining;
            }
        }

        private static void AddDivider(ContainerBuilder container, bool isDivider = true)
        {
            container.AddComponent(new SeparatorBuilder()
                .WithIsDivider(isDivider)
                .WithSpacing(SeparatorSpacingSize.Small));
        }

        private static bool IsHttpMedia(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp);

        private static string SanitizeHeading(string value) =>
            value.Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
