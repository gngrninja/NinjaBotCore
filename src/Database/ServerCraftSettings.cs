#nullable enable

using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public class ServerCraftSettings
    {
        [Key]
        public long DiscordGuildId { get; set; }

        public long? CraftChannelId { get; set; }

        public int MaxOpenTicketsPerUser { get; set; } = 3;

        public int TicketExpirationHours { get; set; } = 48;

        public long? SetById { get; set; }

        [MaxLength(100)]
        public string? SetByName { get; set; }

        public DateTime? TimeSet { get; set; }
    }
}
