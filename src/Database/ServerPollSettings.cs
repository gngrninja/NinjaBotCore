#nullable enable

using System;
using System.ComponentModel.DataAnnotations;

namespace NinjaBotCore.Database
{
    public class ServerPollSettings
    {
        [Key]
        public long DiscordGuildId { get; set; }

        /// <summary>
        /// Channel where poll results are posted. Null means same channel as the poll.
        /// </summary>
        public long? ResultsChannelId { get; set; }

        /// <summary>
        /// Whether to mention voters when a poll closes.
        /// </summary>
        public bool MentionVotersOnClose { get; set; } = false;

        /// <summary>
        /// Whether new polls default to anonymous voting.
        /// </summary>
        public bool DefaultAnonymous { get; set; } = false;

        /// <summary>
        /// Default role IDs that can vote on new polls. Null = everyone can vote.
        /// Comma-separated list of role IDs.
        /// </summary>
        [MaxLength(1000)]
        public string? DefaultAllowedRoleIds { get; set; }

        public long? SetById { get; set; }

        [MaxLength(100)]
        public string? SetByName { get; set; }

        public DateTime? TimeSet { get; set; }
    }
}
