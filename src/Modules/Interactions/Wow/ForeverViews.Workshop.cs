#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Discord;
using NinjaBotCore.Database;
using NinjaBotCore.Models.Wow.Forever;
using NinjaBotCore.Services;

namespace NinjaBotCore.Modules.Interactions.Wow
{
    /// <summary>The crafting, price and camp cards.</summary>
    public static partial class ForeverViews
    {
        public const string CampButtonPrefix = "forever_camp";
        public const string CampJoin = "join";
        public const string CampLeave = "leave";
        public const string CampRefresh = "refresh";

        private const int MaxCrafterLines = 15;
        private const int MaxCamperName = 32;
        // A gap under a tenth of the price is "about the same".
        private const int CloseCall = 10;
        // Under Discord's 4,000 characters of text per message, with a little slack.
        private const int MaxCardChars = 3900;
        private const int MinPlanChars = 150;
        private static readonly Color CampColor = new(0xE8742A);

        private const string RegionalNote = "a regional average, not your own auction house";

        // ===== Craft =====

        /// <summary>
        /// One recipe: what it makes and takes, what the materials cost against buying it, and who in the
        /// server's guild knows it.
        /// </summary>
        /// <param name="guild">The server's Forever guild, or null when none is set and nobody can be listed.</param>
        /// <param name="who">The guild's crafting index, or null while it is being read (see <paramref name="progress"/>) or when it couldn't be.</param>
        public static MessageComponent Craft(ForeverCraftSheet sheet, ForeverGuild? guild, ForeverCraftWho? who, ForeverCraftProgress? progress = null)
        {
            var card = new ContainerBuilder().WithAccentColor(guild != null ? FactionColor(guild.Faction) : ForeverColor);

            var kind = ForeverProfessions.Emoji(sheet.Ref.ProfessionId) + " " + Format.Sanitize(sheet.Ref.ProfessionName);
            if (!string.IsNullOrWhiteSpace(sheet.Ref.Category)) kind += " · " + Format.Sanitize(sheet.Ref.Category);
            card.AddComponent(new TextDisplayBuilder($"# 🛠️ {Heading(sheet.Ref.Name)}\n{kind}"));
            AddDivider(card);

            var priced = false;
            card.AddComponent(new TextDisplayBuilder(Clip(RecipeText(sheet, ref priced), MaxBodyChars / 2)));
            AddDivider(card);
            card.AddComponent(new TextDisplayBuilder(Clip(CraftersText(sheet.Ref, guild, who, progress), MaxBodyChars / 2)));

            var footer = new List<string>();
            if (priced) footer.Add($"{ForeverPrices.Credit} · {RegionalNote}");
            footer.Add("Recipes and crafters from Blizzard's armory, which updates when a character logs out");
            AddFooter(card, string.Join(" · ", footer));
            return new ComponentBuilderV2().AddComponent(card).Build();
        }

        private static string RecipeText(ForeverCraftSheet sheet, ref bool priced)
        {
            var recipe = sheet.Recipe;
            if (recipe == null) return "*Blizzard didn't send what this recipe takes just now. Try again in a few minutes.*";

            var sb = new StringBuilder();
            if (recipe.CraftedItemId is > 0)
            {
                sb.Append("**Makes** ").Append(Linked(recipe.CraftedItemName ?? recipe.Name, ForeverWowhead.Item(recipe.CraftedItemId)));
                if (recipe.MaxQuantity > recipe.MinQuantity) sb.Append($" ×{recipe.MinQuantity:0.#}–{recipe.MaxQuantity:0.#}");
                else if (recipe.MinQuantity > 1) sb.Append($" ×{recipe.MinQuantity:0.#}");
                sb.Append('\n');
            }
            else if (!string.IsNullOrWhiteSpace(recipe.Description))
            {
                sb.Append(Format.Sanitize(recipe.Description.Replace('\r', ' ').Replace('\n', ' ').Trim())).Append('\n');
            }

            if (recipe.Reagents.Count == 0) return sb.Append("*Blizzard lists no materials for this recipe.*").ToString();

            sb.Append("### Materials\n");
            var width = recipe.Reagents.Max(r => r.Quantity.ToString(CultureInfo.InvariantCulture).Length);
            long total = 0;
            var unpriced = new List<string>();
            foreach (var reagent in recipe.Reagents)
            {
                sb.Append('`').Append(reagent.Quantity.ToString(CultureInfo.InvariantCulture).PadLeft(width)).Append(" ×` ")
                    .Append(Linked(reagent.Name, ForeverWowhead.Item(reagent.ItemId)));
                if (sheet.Prices?.Find(reagent.ItemId) is { MarketValue: > 0 } price)
                {
                    var cost = price.MarketValue * reagent.Quantity;
                    total += cost;
                    priced = true;
                    sb.Append(" · ").Append(ForeverMoney.Format(cost));
                    if (reagent.Quantity > 1) sb.Append(" (").Append(ForeverMoney.Format(price.MarketValue)).Append(" each)");
                }
                else if (sheet.Prices != null)
                {
                    unpriced.Add(reagent.Name);
                    sb.Append(" · no price");
                }
                sb.Append('\n');
            }

            if (sheet.Prices == null) return sb.Append("-# TradeSkillMaster didn't send prices just now.").ToString();
            if (total == 0) return sb.Append("-# None of these is on the auction house price list, so they're likely sold by a vendor or bind on pickup.").ToString();

            sb.Append("**Materials** ≈ ").Append(ForeverMoney.Format(total));
            if (unpriced.Count > 0) sb.Append($" plus {unpriced.Count} without a price");
            var made = recipe.AverageQuantity;
            if (made > 1) sb.Append($", so ≈ {ForeverMoney.Format((long)Math.Round(total / made))} each");

            if (sheet.Prices.Find(recipe.CraftedItemId) is { MarketValue: > 0 } crafted)
            {
                priced = true;
                sb.Append(" · **Buying it** ≈ ").Append(ForeverMoney.Format(crafted.MarketValue));
                // Only a full bill of materials says anything about which is cheaper.
                if (unpriced.Count == 0)
                {
                    var perItem = (long)Math.Round(total / made);
                    var gap = crafted.MarketValue - perItem;
                    // Regional averages aren't exact enough to call a few percent either way.
                    if (Math.Abs(gap) * CloseCall < Math.Max(crafted.MarketValue, perItem)) sb.Append("\nAbout the same either way.");
                    else if (gap > 0) sb.Append($"\nCrafting comes out ≈ **{ForeverMoney.Format(gap)}** cheaper{(made > 1 ? " each" : "")}.");
                    else sb.Append($"\nBuying comes out ≈ **{ForeverMoney.Format(-gap)}** cheaper{(made > 1 ? " each" : "")}.");
                }
            }
            sb.Append('\n').Append("-# ").Append(sheet.Region).Append(" market value");
            if (sheet.Prices.ScannedAt is { } scanned) sb.Append(", scanned <t:").Append(scanned.ToUnixTimeSeconds()).Append(":R>");
            sb.Append(". Vendor prices, deposits and the auction house cut aren't counted.");
            return sb.ToString();
        }

        private static string CraftersText(ForeverRecipeRef recipe, ForeverGuild? guild, ForeverCraftWho? who, ForeverCraftProgress? progress)
        {
            if (guild == null)
                return "### Who can make it\nThis server has no Forever guild yet. An officer can set one with `/forever guild set`, and then this lists who knows the recipe.";

            var sb = new StringBuilder();
            if (who == null)
            {
                sb.Append("### Who can make it\n");
                if (progress is { } p)
                {
                    sb.Append("⏳ Reading the guild's professions from the armory");
                    if (p.Total > 0) sb.Append($": **{p.Done:N0} of {p.Total:N0}** characters");
                    sb.Append("…\n-# This takes a moment the first time; the answers are kept for six hours.");
                }
                else
                {
                    sb.Append("Reading the guild's professions took too long. Try again in a few minutes.");
                }
                return sb.ToString();
            }

            var index = who.Index;
            var crafters = index.WhoKnows(recipe.Id);
            sb.Append("### Who can make it");
            if (crafters.Count > 0) sb.Append($" · {crafters.Count:N0}");
            sb.Append('\n');

            if (crafters.Count == 0)
            {
                sb.Append("Nobody in **").Append(Format.Sanitize(guild.Name)).Append("** knows this recipe yet.\n");
            }
            foreach (var (crafter, profession) in crafters.Take(MaxCrafterLines))
            {
                sb.Append(ClassEmoji(crafter.Class)).Append(" **").Append(Format.Sanitize(crafter.FullName)).Append("**");
                if (crafter.Level > 0) sb.Append(" · ").Append(crafter.Level);
                sb.Append(" · ").Append(Format.Sanitize(profession.Name)).Append(' ').Append(profession.Skill);
                if (profession.MaxSkill > 0) sb.Append('/').Append(profession.MaxSkill);
                if (who.SharedBy.TryGetValue((ForeverNames.Normalize(crafter.FirstName), ForeverNames.Normalize(crafter.Surname)), out var owner))
                    sb.Append(" — ").Append(Format.Sanitize(owner));
                sb.Append('\n');
            }
            if (crafters.Count > MaxCrafterLines) sb.Append($"…and {crafters.Count - MaxCrafterLines:N0} more\n");

            sb.Append("-# ");
            if (index.RosterMissed) sb.Append("The armory didn't send the guild's roster, so only characters on this server's roster were read · ");
            sb.Append($"{index.Crafters.Count:N0} of {index.Asked:N0} characters had professions on the armory");
            if (index.Skipped > 0) sb.Append($" · the {index.Skipped:N0} lowest levels weren't read");
            sb.Append(" · read <t:").Append(index.BuiltAt.ToUnixTimeSeconds()).Append(":R>");
            return sb.ToString();
        }

        // ===== Price =====

        public static MessageComponent Price(ForeverPrice price, ForeverPriceSheet sheet)
        {
            var name = string.IsNullOrWhiteSpace(price.Name) ? $"Item {price.ItemId}" : price.Name;
            var card = new ContainerBuilder().WithAccentColor(ForeverColor);

            var heading = new StringBuilder($"# 💰 {Heading(name)}\n{sheet.Region} auction houses");
            if (sheet.ScannedAt is { } scanned) heading.Append(" · scanned <t:").Append(scanned.ToUnixTimeSeconds()).Append(":R>");
            card.AddComponent(new TextDisplayBuilder(heading.ToString()));
            AddDivider(card);

            var sb = new StringBuilder();
            sb.Append("🏷️ **Market value** ").Append(ForeverMoney.Format(price.MarketValue)).Append('\n');
            if (price.AvgSalePrice > 0) sb.Append("🤝 **Usually sells for** ").Append(ForeverMoney.Format(price.AvgSalePrice)).Append('\n');
            if (price.Historical > 0)
            {
                sb.Append("📈 **Its usual price** ").Append(ForeverMoney.Format(price.Historical));
                var change = (price.MarketValue - price.Historical) * 100.0 / price.Historical;
                if (Math.Abs(change) >= 5) sb.Append($" · now **{Math.Abs(change):0}% {(change > 0 ? "above" : "below")}** it");
                sb.Append('\n');
            }
            var trade = new List<string>();
            if (price.SoldPerDay > 0) trade.Add($"**{PerDay(price.SoldPerDay)}** sold a day");
            if (price.SaleRate > 0) trade.Add($"**{Math.Min(price.SaleRate, 1) * 100:0}%** of listings sell");
            if (trade.Count > 0) sb.Append("📦 ").Append(string.Join(" · ", trade)).Append('\n');
            card.AddComponent(new TextDisplayBuilder(sb.ToString().TrimEnd('\n')));

            card.AddComponent(new ActionRowBuilder().WithButton(
                new ButtonBuilder("Wowhead", style: ButtonStyle.Link, url: ForeverWowhead.Item(price.ItemId))));

            AddFooter(card, $"{ForeverPrices.Credit} · {RegionalNote}");
            return new ComponentBuilderV2().AddComponent(card).Build();
        }

        /// <summary>"12,795", "38" or "0.4": a daily count without decimals it doesn't need.</summary>
        private static string PerDay(double value)
            => value >= 10 ? value.ToString("N0", CultureInfo.InvariantCulture) : value.ToString("0.#", CultureInfo.InvariantCulture);

        // ===== Camp =====

        /// <summary>
        /// The public camp card. Its header and its "Who's in" block are also the card's memory: the
        /// title, the host and one mention per camper are read back from them on every click.
        /// </summary>
        public static MessageComponent Camp(ForeverCampState state, IReadOnlyList<ForeverCamper> campers, ForeverCampPlan plan)
        {
            const string footer = "Joining shows your main here · crafts come from Blizzard's armory, which updates when a character logs out · save a character with /forever char add";
            var card = new ContainerBuilder().WithAccentColor(CampColor);
            var header = $"# 🔥 {state.Title}\n{ForeverCamps.HostLead}<@{state.HostId}>";
            card.AddComponent(new TextDisplayBuilder(header));
            AddDivider(card);

            var byUser = campers.GroupBy(c => c.UserId).ToDictionary(g => g.Key, g => g.First());
            var who = new StringBuilder(ForeverCamps.WhoHeading);
            if (state.Members.Count > 0) who.Append(" · ").Append(state.Members.Count);
            who.Append('\n');
            if (state.Members.Count == 0) who.Append("Nobody yet. Press **I'm in** to join.");
            foreach (var member in state.Members)
            {
                who.Append("- <@").Append(member).Append("> · ");
                if (byUser.TryGetValue(member, out var camper))
                {
                    who.Append(ClassEmoji(camper.Class)).Append(" **").Append(Format.Sanitize(Clip(camper.CharacterName, MaxCamperName))).Append("**");
                    var kind = string.Join(" ", new[] { camper.Level > 0 ? camper.Level.ToString() : null, camper.Class?.ToString() }.Where(p => p != null));
                    if (kind.Length > 0) who.Append(" · ").Append(kind);
                }
                else
                {
                    who.Append("no saved character any more");
                }
                who.Append('\n');
            }
            // Never clipped: this block is where the card keeps who is in. Names are kept short so 25 campers fit.
            var whoText = who.ToString().TrimEnd('\n');
            card.AddComponent(new TextDisplayBuilder(whoText));

            // The plan gets whatever room the sign-ups leave of Discord's 4,000 characters.
            var room = MaxCardChars - header.Length - whoText.Length - footer.Length;
            if (campers.Count > 0 && room >= MinPlanChars)
            {
                AddDivider(card);
                card.AddComponent(new TextDisplayBuilder(Clip(CampPlanText(campers, plan), Math.Min(room, MaxBodyChars / 2))));
            }

            card.AddComponent(new ActionRowBuilder()
                .WithButton(new ButtonBuilder("I'm in", $"{CampButtonPrefix}~{CampJoin}", ButtonStyle.Success, emote: new Emoji("🔥"),
                    isDisabled: state.Members.Count >= ForeverCampState.MaxMembers))
                .WithButton(new ButtonBuilder("Leave", $"{CampButtonPrefix}~{CampLeave}", ButtonStyle.Secondary))
                .WithButton(new ButtonBuilder("Refresh", $"{CampButtonPrefix}~{CampRefresh}", ButtonStyle.Secondary, emote: new Emoji("🔄"))));

            AddFooter(card, footer);
            return new ComponentBuilderV2().AddComponent(card).Build();
        }

        private static string CampPlanText(IReadOnlyList<ForeverCamper> campers, ForeverCampPlan plan)
        {
            var sb = new StringBuilder();
            // A few names say who; a full camp's worth would bury the plan.
            string Names(IEnumerable<ForeverCamper> who)
            {
                var names = who.Select(c => Format.Sanitize(Clip(c.CharacterName, MaxCamperName))).Distinct().ToList();
                return names.Count <= 3 ? string.Join(", ", names) : string.Join(", ", names.Take(3)) + $" and {names.Count - 3} more";
            }

            if (plan.Picks.Count > 0)
            {
                sb.Append("### 🎒 Who brings what\n");
                var number = 1;
                foreach (var (feature, camper, recipe) in plan.Picks)
                {
                    sb.Append(number++).Append(". ").Append(ForeverProfessions.Emoji(feature.ProfessionId)).Append(" **").Append(Format.Sanitize(recipe)).Append("** · ")
                        .Append(feature.Effect).Append(" → ").Append(Format.Sanitize(Clip(camper.CharacterName, MaxCamperName))).Append('\n');
                }
                sb.Append("-# One object per player. ");
                sb.Append(plan.UpgradedFire
                    ? "A campfire above the basic one holds five or ten of them."
                    : plan.Picks.Count > ForeverCamp.BasicCapacity
                        ? $"A Basic Campfire holds the first {ForeverCamp.BasicCapacity}; a better campfire holds five or ten."
                        : $"A Basic Campfire holds {ForeverCamp.BasicCapacity}.");
                sb.Append('\n');
            }
            else
            {
                sb.Append("### 🎒 Who brings what\nNobody here knows a camp recipe yet. Each profession teaches one at skill 20.\n");
            }

            sb.Append(plan.FireStarters.Count > 0
                ? $"🔥 **Campfire** {Names(plan.FireStarters.Select(f => f.Camper))} can light it\n"
                : "⚠️ **Campfire** nobody here knows one yet. It's the first Cooking recipe.\n");

            // A class buff in camp makes its object add nothing, which is worth knowing before someone crafts it.
            foreach (var offer in plan.Offers.Where(o => o.Makers.Count > 0 && o.CoveredBy.Count > 0))
            {
                sb.Append("-# ⚠️ ").Append(offer.Feature.Name).Append(" doesn't stack with ").Append(offer.Feature.ReplacedBuff)
                    .Append(", which ").Append(Names(offer.CoveredBy)).Append(" can cast\n");
            }

            var spare = plan.Offers
                .Where(o => o.Makers.Count > 0 && plan.Picks.All(p => !ReferenceEquals(p.Feature, o.Feature)))
                .Select(o => $"{ForeverProfessions.Emoji(o.Feature.ProfessionId)} {o.Feature.Name} ({Names(o.Makers.Select(m => m.Camper))})")
                .ToList();
            if (spare.Count > 0) sb.Append("-# Also possible: ").Append(string.Join(" · ", spare)).Append('\n');

            var missing = plan.Offers.Where(o => o.Makers.Count == 0)
                .Select(o => $"{ForeverProfessions.Emoji(o.Feature.ProfessionId)} {o.Feature.Name} ({o.Feature.Profession})")
                .ToList();
            if (missing.Count > 0) sb.Append("-# Nobody here makes: ").Append(string.Join(" · ", missing)).Append('\n');

            var unknown = campers.Where(c => c.Professions == null).ToList();
            if (unknown.Count > 0)
                sb.Append("-# The armory has no professions for ").Append(Names(unknown)).Append(" yet, so nothing is counted for them\n");
            return sb.ToString().TrimEnd('\n');
        }
    }
}
