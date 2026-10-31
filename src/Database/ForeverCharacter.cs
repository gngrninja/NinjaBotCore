#nullable enable

using System;
using System.ComponentModel.DataAnnotations;
using NinjaBotCore.Models.Wow.Forever;

namespace NinjaBotCore.Database
{
    /// <summary>
    /// A WoW Forever character a Discord user says is theirs. Owned by the user, not a server,
    /// and private until shared to a server's roster.
    /// </summary>
    public class ForeverCharacter
    {
        [Key]
        public long Id { get; set; }

        public long UserId { get; set; }

        [Required]
        [MaxLength(64)]
        public string FirstName { get; set; } = string.Empty;

        [Required]
        [MaxLength(64)]
        public string LastName { get; set; } = string.Empty;

        [Required]
        [MaxLength(64)]
        public string NormalizedFirstName { get; set; } = string.Empty;

        [Required]
        [MaxLength(64)]
        public string NormalizedLastName { get; set; } = string.Empty;

        public ForeverRegion Region { get; set; }

        public ForeverRuleset Ruleset { get; set; }

        public ForeverFaction Faction { get; set; }

        public ForeverClass? Class { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime UpdatedAt { get; set; }

        public string FullName => ForeverNames.FullName(FirstName, LastName);
    }
}
