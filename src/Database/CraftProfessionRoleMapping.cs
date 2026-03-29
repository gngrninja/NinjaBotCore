#nullable enable

using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public class CraftProfessionRoleMapping
    {
        [Key]
        public long Id { get; set; }

        public long GuildId { get; set; }

        [Required]
        [MaxLength(100)]
        public string Profession { get; set; } = string.Empty;

        public long RoleId { get; set; }

        [MaxLength(100)]
        public string? RoleName { get; set; }

        public long? SetById { get; set; }

        [MaxLength(100)]
        public string? SetByName { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
