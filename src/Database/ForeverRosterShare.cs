#nullable enable

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotCore.Database
{
    /// <summary>A user's explicit choice to show one character on one Discord server's Forever roster.</summary>
    public class ForeverRosterShare
    {
        [Key]
        public long Id { get; set; }

        public long DiscordGuildId { get; set; }

        public long UserId { get; set; }

        public long CharacterId { get; set; }

        [ForeignKey(nameof(CharacterId))]
        public virtual ForeverCharacter? Character { get; set; }

        public DateTime SharedAt { get; set; }
    }
}
