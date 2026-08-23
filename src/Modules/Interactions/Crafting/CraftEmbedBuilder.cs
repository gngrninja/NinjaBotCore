using System;
using System.Collections.Generic;
using System.Linq;
using Discord;
using NinjaBotCore.Common;
using NinjaBotCore.Database;
using NinjaBotCore.Modules.Interactions.Wow.CharViews;

namespace NinjaBotCore.Modules.Interactions.Crafting
{
    public static class CraftEmbedBuilder
    {
        public const int MaxItemNameLength = 256;

        public static bool IsValidItemName(string itemName) =>
            !string.IsNullOrWhiteSpace(itemName)
            && itemName.Trim().Length <= MaxItemNameLength;
        private static readonly Color OpenColor = Color.Blue;
        private static readonly Color ClaimedColor = new Color(255, 165, 0);
        private static readonly Color CraftedColor = new Color(144, 238, 144);
        private static readonly Color CompleteColor = Color.Green;
        private static readonly Color CancelledColor = Color.Red;
        private static readonly Color ExpiredColor = Color.LightGrey;

        public static ComponentBuilderV2 BuildTicketCard(CraftTicket ticket, string preface = null)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            return WowCardV2.FromEmbed(
                BuildTicketEmbed(ticket),
                BuildComponents(ticket).Build(),
                preface);
        }

        public static ComponentBuilderV2 BuildTicketListCard(
            IReadOnlyCollection<CraftTicket> tickets,
            string title,
            MessageComponent controls = null)
        {
            var allRows = (tickets ?? Array.Empty<CraftTicket>()).ToList();
            var rows = allRows.Take(10).ToList();
            var embed = new EmbedBuilder()
                .WithTitle(title)
                .WithColor(Color.Blue)
                .WithFooter($"Showing {rows.Count} of {allRows.Count} ticket(s)")
                .WithCurrentTimestamp();

            if (rows.Count == 0)
            {
                embed.WithDescription("*No crafting requests match this filter.*");
            }
            else if (allRows.Count > rows.Count)
            {
                embed.WithDescription(
                    $"*Showing the first {rows.Count} requests. Use the profession filter to narrow {allRows.Count} active tickets.*");
            }

            foreach (var ticket in rows)
            {
                var statusEmoji = ticket.Status switch
                {
                    "Open" => "\uD83D\uDFE2",
                    "Claimed" => "\uD83D\uDD35",
                    "Crafted" => "\uD83D\uDFE1",
                    "Complete" => "\u2705",
                    "Expired" => "\u23F0",
                    "Cancelled" => "\u274C",
                    _ => "\u26AA"
                };

                var createdUnix = ((DateTimeOffset)ticket.CreatedAt).ToUnixTimeSeconds();
                var details = $"Status: {ticket.Status} | Requester: <@{(ulong)ticket.RequesterId}> | <t:{createdUnix}:R>";
                if (ticket.CrafterId.HasValue)
                {
                    details += $" | Crafter: <@{(ulong)ticket.CrafterId.Value}>";
                }

                var itemName = string.IsNullOrWhiteSpace(ticket.ItemName) ? "Unknown item" : ticket.ItemName.Trim();
                var fieldName = BoundDisplay(
                    $"{statusEmoji} #{ticket.Id} — {itemName}",
                    80,
                    $"{statusEmoji} #{ticket.Id} — Unknown item");

                embed.AddField(fieldName, details, inline: false);
            }

            return WowCardV2.FromEmbed(embed, controls);
        }

        public static string BuildThreadPreface(CraftTicket ticket, long? professionRoleId = null)
        {
            if (ticket == null)
            {
                throw new ArgumentNullException(nameof(ticket));
            }

            var requester = $"<@{(ulong)ticket.RequesterId}>";
            var crafter = ticket.CrafterId.HasValue
                ? $"<@{(ulong)ticket.CrafterId.Value}>"
                : "unassigned";
            var role = professionRoleId.HasValue
                ? $" · <@&{(ulong)professionRoleId.Value}>"
                : string.Empty;
            return ticket.Status switch
            {
                "Claimed" => $"**In progress** · Requester {requester} · Crafter {crafter}",
                "Crafted" => $"**Awaiting trade** · Requester {requester} · Crafter {crafter}",
                "Complete" => $"**Complete** · Requester {requester} · Crafter {crafter}",
                "Cancelled" => $"**Cancelled** · Requester {requester}",
                "Expired" => $"**Expired** · Requester {requester}",
                _ => $"{requester} is looking for a crafter.{role}"
            };
        }

        public static EmbedBuilder BuildTicketEmbed(CraftTicket ticket)
        {
            return ticket.Status switch
            {
                "Open" => BuildOpenEmbed(ticket),
                "Claimed" => BuildClaimedEmbed(ticket),
                "Crafted" => BuildCraftedEmbed(ticket),
                "Complete" => BuildCompleteEmbed(ticket),
                "Cancelled" => BuildCancelledEmbed(ticket),
                "Expired" => BuildExpiredEmbed(ticket),
                _ => BuildOpenEmbed(ticket)
            };
        }

        public static ComponentBuilder BuildComponents(CraftTicket ticket)
        {
            return ticket.Status switch
            {
                "Open" => BuildOpenComponents(ticket),
                "Claimed" => BuildClaimedComponents(ticket),
                "Crafted" => BuildCraftedComponents(ticket),
                _ => BuildDisabledComponents(ticket)
            };
        }

        private static EmbedBuilder BuildOpenEmbed(CraftTicket ticket)
        {
            var embed = BuildBaseEmbed(ticket, OpenColor, "Open");

            embed.AddField("Status", "Open", inline: true);

            if (ticket.ExpiresAt.HasValue)
            {
                var unixTime = ((DateTimeOffset)ticket.ExpiresAt.Value).ToUnixTimeSeconds();
                embed.AddField("Expires", $"<t:{unixTime}:R>", inline: true);
            }

            if (!string.IsNullOrEmpty(ticket.Note))
                embed.AddField("Note", ticket.Note);

            return embed;
        }

        private static EmbedBuilder BuildClaimedEmbed(CraftTicket ticket)
        {
            var embed = BuildBaseEmbed(ticket, ClaimedColor, "In Progress");

            embed.AddField("Status", "In Progress", inline: true);
            if (ticket.CrafterId.HasValue)
                embed.AddField("Crafter", $"<@{(ulong)ticket.CrafterId.Value}>", inline: true);

            if (!string.IsNullOrEmpty(ticket.Note))
                embed.AddField("Note", ticket.Note);

            return embed;
        }

        private static EmbedBuilder BuildCraftedEmbed(CraftTicket ticket)
        {
            var embed = BuildBaseEmbed(ticket, CraftedColor, "Crafted - Awaiting Trade");

            embed.AddField("Status", "Crafted - Awaiting Trade", inline: true);
            if (ticket.CrafterId.HasValue)
                embed.AddField("Crafter", $"<@{(ulong)ticket.CrafterId.Value}>", inline: true);

            if (!string.IsNullOrEmpty(ticket.Note))
                embed.AddField("Note", ticket.Note);

            return embed;
        }

        private static EmbedBuilder BuildCompleteEmbed(CraftTicket ticket)
        {
            var embed = BuildBaseEmbed(ticket, CompleteColor, "Complete");

            embed.AddField("Status", "Complete", inline: true);
            if (ticket.CrafterId.HasValue)
                embed.AddField("Crafter", $"<@{(ulong)ticket.CrafterId.Value}>", inline: true);

            if (ticket.CompletedAt.HasValue)
            {
                var unixTime = ((DateTimeOffset)ticket.CompletedAt.Value).ToUnixTimeSeconds();
                embed.AddField("Completed", $"<t:{unixTime}:R>", inline: true);
            }

            return embed;
        }

        private static EmbedBuilder BuildCancelledEmbed(CraftTicket ticket)
        {
            var embed = BuildBaseEmbed(ticket, CancelledColor, "Cancelled");
            embed.AddField("Status", "Cancelled", inline: true);
            return embed;
        }

        private static EmbedBuilder BuildExpiredEmbed(CraftTicket ticket)
        {
            var embed = BuildBaseEmbed(ticket, ExpiredColor, "Expired");
            embed.AddField("Status", "Expired", inline: true);
            return embed;
        }

        private static EmbedBuilder BuildBaseEmbed(CraftTicket ticket, Color color, string statusLabel)
        {
            var footerHint = string.IsNullOrEmpty(ticket.RequesterRealm) && statusLabel == "Open"
                ? " | Use /char to show your realm"
                : "";

            var embed = new EmbedBuilder()
                .WithTitle(BoundDisplay(ticket.ItemName, 256, "Unknown item"))
                .WithColor(color)
                .WithFooter($"Ticket #{ticket.Id} | Requested by {ticket.RequesterName}{footerHint}")
                .WithTimestamp(ticket.CreatedAt);

            if (!string.IsNullOrEmpty(ticket.ItemIconUrl))
                embed.WithThumbnailUrl(ticket.ItemIconUrl);

            if (!ticket.BlizzardItemId.HasValue)
                embed.WithDescription("*Item unverified*");

            embed.AddField("Requested by", $"<@{(ulong)ticket.RequesterId}>", inline: true);

            if (!string.IsNullOrEmpty(ticket.Profession))
                embed.AddField("Profession", ticket.Profession, inline: true);

            if (!string.IsNullOrEmpty(ticket.RequesterRealm))
            {
                embed.AddField("Realm", ticket.RequesterRealm, inline: true);
            }

            if (!string.IsNullOrEmpty(ticket.QualityDesired))
                embed.AddField("Quality", ticket.QualityDesired, inline: true);

            if (!string.IsNullOrEmpty(ticket.MaterialsStatus))
                embed.AddField("Materials", ticket.MaterialsStatus, inline: true);

            if (!string.IsNullOrEmpty(ticket.Commission))
                embed.AddField("Commission", ticket.Commission, inline: true);

            if (!string.IsNullOrEmpty(ticket.ConnectedRealms))
            {
                var prefix = "Available to crafters on: ";
                var maxRealmLen = 1024 - prefix.Length - 3;
                var realmList = ticket.ConnectedRealms.Length > maxRealmLen
                    ? ticket.ConnectedRealms[..maxRealmLen] + "..."
                    : ticket.ConnectedRealms;
                embed.AddField("Personal Orders", $"{prefix}{realmList}", inline: false);
            }

            return embed;
        }

        private static ComponentBuilder BuildOpenComponents(CraftTicket ticket)
        {
            var builder = new ComponentBuilder();
            builder.WithButton("I can craft this", $"{ModalConstants.CraftClaimPrefix}{ticket.Id}",
                ButtonStyle.Primary, emote: new Emoji("\u2692\uFE0F"));
            builder.WithButton("Close request", $"{ModalConstants.CraftGotItPrefix}{ticket.Id}",
                ButtonStyle.Success, emote: new Emoji("\u2705"));
            builder.WithButton("Cancel", $"{ModalConstants.CraftCancelPrefix}{ticket.Id}",
                ButtonStyle.Danger, emote: new Emoji("\u274C"));
            AddWowheadButton(builder, ticket);
            AddJoinRoleButton(builder, ticket);
            return builder;
        }

        private static ComponentBuilder BuildClaimedComponents(CraftTicket ticket)
        {
            var builder = new ComponentBuilder();
            builder.WithButton("Mark as Crafted", $"{ModalConstants.CraftCraftedPrefix}{ticket.Id}",
                ButtonStyle.Success, emote: new Emoji("\u2705"));
            builder.WithButton("Cancel", $"{ModalConstants.CraftCancelPrefix}{ticket.Id}",
                ButtonStyle.Danger, emote: new Emoji("\u274C"));
            AddWowheadButton(builder, ticket);
            AddJoinRoleButton(builder, ticket);
            return builder;
        }

        private static ComponentBuilder BuildCraftedComponents(CraftTicket ticket)
        {
            var builder = new ComponentBuilder();
            builder.WithButton("Item Received", $"{ModalConstants.CraftCompletePrefix}{ticket.Id}",
                ButtonStyle.Success, emote: new Emoji("\uD83E\uDD1D"));
            builder.WithButton("Cancel", $"{ModalConstants.CraftCancelPrefix}{ticket.Id}",
                ButtonStyle.Danger, emote: new Emoji("\u274C"));
            AddWowheadButton(builder, ticket);
            AddJoinRoleButton(builder, ticket);
            return builder;
        }

        private static ComponentBuilder BuildDisabledComponents(CraftTicket ticket)
        {
            var builder = new ComponentBuilder();
            var label = ticket.Status switch
            {
                "Complete" => "Completed",
                "Cancelled" => "Cancelled",
                "Expired" => "Expired",
                _ => "Closed"
            };
            builder.WithButton(label, "craft_closed", ButtonStyle.Secondary, disabled: true);
            AddWowheadButton(builder, ticket);
            return builder;
        }

        private static void AddWowheadButton(ComponentBuilder builder, CraftTicket ticket)
        {
            var url = ticket.BlizzardItemId is > 0
                ? $"https://www.wowhead.com/item={ticket.BlizzardItemId.Value}"
                : $"https://www.wowhead.com/search?q={Uri.EscapeDataString(BoundDisplay(ticket.ItemName, 50, "Unknown item"))}";
            builder.WithButton("Open on Wowhead",
                url: url,
                style: ButtonStyle.Link, row: 1);
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

            return display[..cutAt] + "…";
        }

        private static void AddJoinRoleButton(ComponentBuilder builder, CraftTicket ticket)
        {
            if (!string.IsNullOrEmpty(ticket.Profession))
            {
                var prefix = ModalConstants.CraftJoinRolePrefix;
                var maxLen = 100 - prefix.Length;
                var prof = ticket.Profession.Length > maxLen ? ticket.Profession[..maxLen] : ticket.Profession;
                builder.WithButton($"Get {ticket.Profession} notifications",
                    $"{prefix}{prof}",
                    ButtonStyle.Secondary, emote: new Emoji("\uD83D\uDD14"), row: 2);
            }
        }
    }
}
