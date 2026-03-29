using System;
using Discord;
using NinjaBotCore.Common;
using NinjaBotCore.Database;

namespace NinjaBotCore.Modules.Interactions.Crafting
{
    public static class CraftEmbedBuilder
    {
        private static readonly Color OpenColor = Color.Blue;
        private static readonly Color ClaimedColor = new Color(255, 165, 0);
        private static readonly Color CraftedColor = new Color(144, 238, 144);
        private static readonly Color CompleteColor = Color.Green;
        private static readonly Color CancelledColor = Color.Red;
        private static readonly Color ExpiredColor = Color.LightGrey;

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
                .WithTitle(ticket.ItemName)
                .WithColor(color)
                .WithFooter($"Ticket #{ticket.Id} | Requested by {ticket.RequesterName}{footerHint}")
                .WithTimestamp(ticket.CreatedAt);

            if (ticket.BlizzardItemId.HasValue)
            {
                embed.WithUrl($"https://www.wowhead.com/item={ticket.BlizzardItemId}");

                if (!string.IsNullOrEmpty(ticket.ItemIconUrl))
                    embed.WithThumbnailUrl(ticket.ItemIconUrl);
            }
            else
            {
                embed.WithDescription("*Item unverified*");
            }

            embed.AddField("Requested by", $"<@{(ulong)ticket.RequesterId}>", inline: true);

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
            builder.WithButton("Cancel", $"{ModalConstants.CraftCancelPrefix}{ticket.Id}",
                ButtonStyle.Danger, emote: new Emoji("\u274C"));
            AddWowheadButton(builder, ticket);
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
            if (ticket.BlizzardItemId.HasValue)
            {
                builder.WithButton("View on Wowhead",
                    url: $"https://www.wowhead.com/item={ticket.BlizzardItemId}",
                    style: ButtonStyle.Link, row: 1);
            }
        }
    }
}
