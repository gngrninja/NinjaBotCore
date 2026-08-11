using Discord;
using NinjaBotCore.Models.Wow;
using NinjaBotCore.Services.Gearing;
using System;
using System.Collections.Generic;
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
                    : $"⬆️ **{recommendation.SlotLabel}** — {recommendation.CurrentItemName} ({BuildItemDetails(recommendation.CurrentItemLevel, recommendation.TrackLabel)})");
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
                        var itemSummary = slot.IsMissing
                            ? "Empty slot"
                            : $"{slot.ItemName} ({BuildItemDetails(slot.ItemLevel, slot.TrackLabel)})";
                        description.AppendLine($"{index + 2}. {slot.SlotLabel} — {itemSummary}{setMarker}");
                    }
                }
            }

            if (assessment?.UpgradeInPlaceSlots?.Count > 0)
            {
                description.AppendLine();
                description.AppendLine("**Upgrade in Place**");
                foreach (var slot in assessment.UpgradeInPlaceSlots.Take(3))
                {
                    description.AppendLine($"• **{slot.SlotLabel}:** {slot.UpgradeAction}");
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
            description.AppendLine("**Spec Resources**");
            description.AppendLine($"[Wowhead Overview]({BuildWowheadOverviewUrl(armorySummary)}) • [Wowhead Gear]({BuildWowheadGearUrl(armorySummary)}) • [Archon M+]({BuildArchonMythicPlusUrl(armorySummary)})");
            description.AppendLine($"[Raidbots Top Gear]({BuildRaidbotsUrl(charInfo)}) — compare trinkets, special effects, and close choices");

            var embed = new EmbedBuilder()
                .WithTitle($"Upgrade Analysis — {characterName}")
                .WithDescription(description.ToString())
                .WithColor(new Color(0, 200, 150))
                .WithFooter("Season 2 track data (12.1.0.69214) • Exact only on bonus match • Verify currency in game • Sim close choices");

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
            return $"https://www.raidbots.com/simbot/topgear?region={region}&realm={realm}&name={name}";
        }

        public static string BuildWowheadOverviewUrl(ArmorySummary summary)
        {
            var classSlug = BuildSlug(summary?.CharacterClass);
            var specSlug = BuildSlug(summary?.ActiveSpec);
            var role = GetRole(summary?.ActiveSpec);
            return $"https://www.wowhead.com/guide/classes/{classSlug}/{specSlug}/overview-pve-{role}";
        }

        public static string BuildWowheadGearUrl(ArmorySummary summary)
        {
            var classSlug = BuildSlug(summary?.CharacterClass);
            var specSlug = BuildSlug(summary?.ActiveSpec);
            return $"https://www.wowhead.com/guide/classes/{classSlug}/{specSlug}/bis-gear";
        }

        public static string BuildArchonMythicPlusUrl(ArmorySummary summary)
        {
            var classSlug = BuildSlug(summary?.CharacterClass);
            var specSlug = BuildSlug(summary?.ActiveSpec);
            return $"https://www.archon.gg/wow/builds/{specSlug}/{classSlug}/mythic-plus/overview/10/all-dungeons/this-week";
        }

        private static string BuildItemDetails(int itemLevel, string trackLabel) =>
            string.IsNullOrWhiteSpace(trackLabel) ? itemLevel.ToString() : $"{itemLevel} · {trackLabel}";

        private static string BuildSlug(ArmoryType value)
        {
            var source = !string.IsNullOrWhiteSpace(value?.Type) ? value.Type : value?.Name ?? "unknown";
            return source.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        }

        private static string GetRole(ArmoryType specialization)
        {
            var spec = (!string.IsNullOrWhiteSpace(specialization?.Type)
                ? specialization.Type
                : specialization?.Name ?? string.Empty).ToUpperInvariant().Replace(' ', '_');
            if (TankSpecs.Contains(spec))
            {
                return "tank";
            }

            return HealerSpecs.Contains(spec) ? "healer" : "dps";
        }

        private static readonly HashSet<string> TankSpecs = new(StringComparer.OrdinalIgnoreCase)
        {
            "BLOOD", "VENGEANCE", "GUARDIAN", "BREWMASTER", "PROTECTION"
        };

        private static readonly HashSet<string> HealerSpecs = new(StringComparer.OrdinalIgnoreCase)
        {
            "DISCIPLINE", "HOLY", "MISTWEAVER", "PRESERVATION", "RESTORATION"
        };
    }
}
