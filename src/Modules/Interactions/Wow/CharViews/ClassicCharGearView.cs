using Discord;
using NinjaBotCore.Models.Wow;
using System.Text;

namespace NinjaBotCore.Modules.Interactions.Wow.CharViews
{
    /// <summary>
    /// Builds the Gear view embed for Classic WoW character profiles from Classic Raider.IO data
    /// </summary>
    public static class ClassicCharGearView
    {
        private static readonly (string Property, string Label)[] SlotOrder = new[]
        {
            ("Head", "Head"),
            ("Neck", "Neck"),
            ("Shoulder", "Shoulder"),
            ("Back", "Back"),
            ("Chest", "Chest"),
            ("Wrist", "Wrist"),
            ("Hands", "Hands"),
            ("Waist", "Waist"),
            ("Legs", "Legs"),
            ("Feet", "Feet"),
            ("Finger1", "Ring 1"),
            ("Finger2", "Ring 2"),
            ("Trinket1", "Trinket 1"),
            ("Trinket2", "Trinket 2"),
            ("Mainhand", "Main Hand"),
            ("Offhand", "Off Hand"),
            ("Ranged", "Ranged")
        };

        public static EmbedBuilder Build(ClassicRaiderIOModels.ClassicCharProfile profile)
        {
            var embed = new EmbedBuilder();
            var sb = new StringBuilder();

            embed.Title = $"Gear - {profile.Name}";
            embed.WithColor(ClassicCharOverviewView.GetFactionColor(profile.Faction));

            // Item Level
            if (profile.Gear != null)
            {
                var equipped = profile.Gear.ItemLevelEquipped;
                var total = profile.Gear.ItemLevelTotal;

                if (total > equipped && total > 0)
                {
                    sb.AppendLine($"**Item Level:** {equipped} / {total}");
                }
                else if (equipped > 0)
                {
                    sb.AppendLine($"**Item Level:** {equipped}");
                }
                sb.AppendLine();
            }

            // Gear slots
            if (profile.Gear?.Items != null)
            {
                var items = profile.Gear.Items;

                foreach (var (property, label) in SlotOrder)
                {
                    var item = GetItemBySlot(items, property);
                    if (item == null) continue;

                    var qualityEmoji = GetQualityEmoji(item.ItemQuality);
                    sb.AppendLine($"{qualityEmoji} **{label}:** {item.Name} ({item.ItemLevel})");
                }
            }
            else
            {
                sb.AppendLine("*No gear data available*");
            }

            embed.Description = sb.ToString();

            // Thumbnail
            if (profile.ThumbnailUrl != null)
            {
                embed.ThumbnailUrl = profile.ThumbnailUrl.AbsoluteUri;
            }

            // Footer
            embed.Footer = new EmbedFooterBuilder
            {
                Text = $"{profile.Realm} ({profile.Region?.ToUpper()}) | Classic"
            };

            return embed;
        }

        private static ClassicRaiderIOModels.ClassicItemDetail GetItemBySlot(
            ClassicRaiderIOModels.ClassicGearItem items, string slot)
        {
            return slot switch
            {
                "Head" => items.Head,
                "Neck" => items.Neck,
                "Shoulder" => items.Shoulder,
                "Back" => items.Back,
                "Chest" => items.Chest,
                "Waist" => items.Waist,
                "Wrist" => items.Wrist,
                "Hands" => items.Hands,
                "Legs" => items.Legs,
                "Feet" => items.Feet,
                "Finger1" => items.Finger1,
                "Finger2" => items.Finger2,
                "Trinket1" => items.Trinket1,
                "Trinket2" => items.Trinket2,
                "Mainhand" => items.Mainhand,
                "Offhand" => items.Offhand,
                "Ranged" => items.Ranged,
                _ => null
            };
        }

        private static string GetQualityEmoji(long quality)
        {
            return quality switch
            {
                5 => "\U0001F7E0",   // Orange - Legendary
                4 => "\U0001F7E3",   // Purple - Epic
                3 => "\U0001F535",   // Blue - Rare
                2 => "\U0001F7E2",   // Green - Uncommon
                1 => "\u26AA",       // White - Common
                0 => "\u26AA",       // Gray - Poor (same as common visually)
                _ => "\u26AA"
            };
        }
    }
}
