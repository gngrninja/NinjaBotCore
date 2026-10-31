#nullable enable

using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NinjaBotCore.Database
{
    /// <summary>A user's Forever main in one Discord server. Never set implicitly.</summary>
    public class ForeverMain
    {
        [Key]
        public long Id { get; set; }

        public long DiscordGuildId { get; set; }

        public long UserId { get; set; }

        public long CharacterId { get; set; }

        [ForeignKey(nameof(CharacterId))]
        public virtual ForeverCharacter? Character { get; set; }

        public DateTime SetAt { get; set; }
    }
}
