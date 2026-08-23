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
        private const int TotalTextDisplayLimit = 4000;
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
            var textBudget = new TextDisplayBudget(TotalTextDisplayLimit);

            AddHeader(container, embed.Title, embed.Thumbnail?.Url, textBudget);
            AddDivider(container);

            if (!string.IsNullOrWhiteSpace(preface) && textBudget.HasRemaining)
            {
                AddText(container, preface, textBudget);
                AddDivider(container, isDivider: false);
            }

            AddText(container, embed.Description, textBudget);
            AddFields(container, embed.Fields, textBudget);

            var imageUrl = embed.Image?.Url;
            if (IsHttpMedia(imageUrl))
            {
                container.AddComponent(new MediaGalleryBuilder()
                    .AddItem(imageUrl, embed.Title));
            }

            var footerText = embed.Footer?.Text;
            if (!string.IsNullOrWhiteSpace(footerText) && textBudget.HasRemaining)
            {
                AddDivider(container);
                AddText(container, $"-# {footerText}", textBudget);
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

        private static void AddHeader(
            ContainerBuilder container,
            string title,
            string thumbnailUrl,
            TextDisplayBudget textBudget)
        {
            var heading = $"# {SanitizeHeading(string.IsNullOrWhiteSpace(title) ? "NinjaBot" : title)}";
            var budgetedHeading = textBudget.Take(heading);
            if (string.IsNullOrEmpty(budgetedHeading))
            {
                return;
            }

            if (IsHttpMedia(thumbnailUrl))
            {
                container.AddComponent(new SectionBuilder(
                    new ThumbnailBuilder(new UnfurledMediaItemProperties(thumbnailUrl), title),
                    new TextDisplayBuilder().WithContent(budgetedHeading)));
                return;
            }

            container.AddComponent(new TextDisplayBuilder().WithContent(budgetedHeading));
        }

        private static void AddFields(
            ContainerBuilder container,
            IReadOnlyCollection<EmbedField> fields,
            TextDisplayBudget textBudget)
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

            AddText(container, string.Join("\n", lines), textBudget);
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

        private static void AddText(
            ContainerBuilder container,
            string content,
            TextDisplayBudget textBudget)
        {
            if (string.IsNullOrWhiteSpace(content) || !textBudget.HasRemaining)
            {
                return;
            }

            var budgetedContent = textBudget.Take(content);
            if (!string.IsNullOrEmpty(budgetedContent))
            {
                container.AddComponent(new TextDisplayBuilder().WithContent(budgetedContent));
            }
        }

        private sealed class TextDisplayBudget
        {
            public TextDisplayBudget(int total)
            {
                Remaining = total;
            }

            public int Remaining { get; private set; }
            public bool HasRemaining => Remaining > 0;

            public string Take(string content)
            {
                if (string.IsNullOrEmpty(content) || Remaining <= 0)
                {
                    return null;
                }

                if (content.Length <= Remaining)
                {
                    Remaining -= content.Length;
                    return content;
                }

                if (Remaining == 1)
                {
                    Remaining = 0;
                    return "…";
                }

                var cutAt = Remaining - 1;
                if (cutAt > 0
                    && cutAt < content.Length
                    && char.IsHighSurrogate(content[cutAt - 1])
                    && char.IsLowSurrogate(content[cutAt]))
                {
                    cutAt--;
                }

                var truncated = content.Substring(0, cutAt) + "…";
                Remaining = 0;
                return truncated;
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
