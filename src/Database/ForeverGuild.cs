#nullable enable

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NinjaBotCore.Models.Wow.Forever;

namespace NinjaBotCore.Database
{
    /// <summary>The WoW Forever guild a Discord server declares. Nothing proves the server is that guild.</summary>
    public class ForeverGuild
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public long DiscordGuildId { get; set; }

        [Required]
        [MaxLength(64)]
        public string Name { get; set; } = string.Empty;

        public ForeverRegion Region { get; set; }

        public ForeverRuleset Ruleset { get; set; }

        public ForeverFaction Faction { get; set; }

        public const int MaxArmorySlugLength = 200;

        /// <summary>The guild's slug on Blizzard's armory. Null when the armory didn't have it, so the guild is as typed.</summary>
        [MaxLength(MaxArmorySlugLength)]
        public string? ArmorySlug { get; set; }

        /// <summary>Members can only share characters to the roster while this is on.</summary>
        public bool RosterEnabled { get; set; }

        public long? SetById { get; set; }

        [MaxLength(100)]
        public string? SetByName { get; set; }

        public DateTime? TimeSet { get; set; }
    }
}
