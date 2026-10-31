#nullable enable

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NinjaBotCore.Models.Wow.Forever;

namespace NinjaBotCore.Database
{
    /// <summary>The WoW Forever guild a Discord server declares. Self-reported, not verified.</summary>
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

        /// <summary>Members can only share characters to the roster while this is on.</summary>
        public bool RosterEnabled { get; set; }

        public long? SetById { get; set; }

        [MaxLength(100)]
        public string? SetByName { get; set; }

        public DateTime? TimeSet { get; set; }
    }
}
