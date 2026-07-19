using Discord;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Services.Gearing;
using System;
using System.Linq;
using System.Text;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    public static class CharUpgradeView
    {
        public static EmbedBuilder Build(
            CharacterInfo charInfo,
            ArmorySummary armorySummary,
            ArmoryMedia armoryMedia,
            GearAssessment assessment)
        {
            var className = armorySummary?.CharacterClass?.Name ?? "Unknown Class";
            var specName = armorySummary?.ActiveSpec?.Name ?? "Unknown Spec";
            var characterName = armorySummary?.Name ?? charInfo?.Name ?? "Character";
            var description = new StringBuilder();

            description.AppendLine($"**{specName} {className}**");
            if (assessment?.EquippedItemLevel > 0)
            {
                description.AppendLine($"**Item Level:** {assessment.EquippedItemLevel} / {assessment.AverageItemLevel}");
            }

            if (assessment?.OverallRecommendation == null)
            {
                description.AppendLine();
                description.AppendLine("No equipped gear data is available for upgrade analysis.");
            }
            else
            {
                var recommendation = assessment.OverallRecommendation;
                description.AppendLine();
                description.AppendLine("**Overall Priority**");
                description.AppendLine(recommendation.IsMissing
                    ? $"⬆️ **{recommendation.SlotLabel}** — Empty slot"
                    : $"⬆️ **{recommendation.SlotLabel}** — {recommendation.CurrentItemName} ({recommendation.CurrentItemLevel})");
                if (recommendation.IsMissing)
                {
                    description.AppendLine("Equip an item in this slot first; almost any current-content reward will be an immediate upgrade.");
                }
                else if (recommendation.ItemLevelGap > 0)
                {
                    description.AppendLine($"This slot trails your equipped average by **{recommendation.ItemLevelGap} item levels**.");
                }
                else
                {
                    description.AppendLine("This is currently your lowest equipped slot.");
                }
                description.AppendLine(recommendation.NextAction);
                if (!string.IsNullOrWhiteSpace(recommendation.Caution))
                {
                    description.AppendLine($"⚠️ {recommendation.Caution}");
                }

                var otherPriorities = assessment.PrioritySlots.Skip(1).Take(4).ToList();
                if (otherPriorities.Count > 0)
                {
                    description.AppendLine();
                    description.AppendLine("**Next Slots**");
                    for (var index = 0; index < otherPriorities.Count; index++)
                    {
                        var slot = otherPriorities[index];
                        var setMarker = slot.IsSetPiece ? " • set" : string.Empty;
                        var itemSummary = slot.IsMissing ? "Empty slot" : $"{slot.ItemName} ({slot.ItemLevel})";
                        description.AppendLine($"{index + 2}. {slot.SlotLabel} — {itemSummary}{setMarker}");
                    }
                }
            }

            if (assessment?.MissingEnchantSlots?.Count > 0 || assessment?.EmptySocketSlots?.Count > 0)
            {
                description.AppendLine();
                description.AppendLine("**Immediate Fixes**");
                if (assessment.MissingEnchantSlots.Count > 0)
                {
                    description.AppendLine($"⚠️ Missing enchants: {string.Join(", ", assessment.MissingEnchantSlots)}");
                }
                if (assessment.EmptySocketSlots.Count > 0)
                {
                    description.AppendLine($"⚠️ Empty sockets: {string.Join(", ", assessment.EmptySocketSlots)}");
                }
            }

            description.AppendLine();
            description.AppendLine($"[Run an exact Raidbots sim]({BuildRaidbotsUrl(charInfo)})");

            var embed = new EmbedBuilder()
                .WithTitle($"Upgrade Analysis — {characterName}")
                .WithDescription(description.ToString())
                .WithColor(new Color(0, 200, 150))
                .WithFooter("General upgrade guidance • Sim trinkets, special-effect items, and close choices");

            var avatar = armoryMedia?.Assets?.FirstOrDefault(asset => asset.Key == "avatar")?.Value;
            if (!string.IsNullOrWhiteSpace(avatar))
            {
                embed.WithThumbnailUrl(avatar);
            }

            return embed;
        }

        public static string BuildRaidbotsUrl(CharacterInfo charInfo)
        {
            var region = Uri.EscapeDataString(charInfo?.Region?.ToLowerInvariant() ?? "us");
            var realm = Uri.EscapeDataString(charInfo?.RealmSlug?.ToLowerInvariant() ?? string.Empty);
            var name = Uri.EscapeDataString(charInfo?.Name ?? string.Empty);
            return $"https://www.raidbots.com/simbot/quick?region={region}&realm={realm}&name={name}";
        }
    }
}
