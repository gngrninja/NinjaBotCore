namespace NinjaBotCore.Common
{
    /// <summary>
    /// Constants for modal and component custom IDs used across the bot.
    /// These are shared between InteractionHandler (for skipping) and UserInteractions (for handling).
    /// </summary>
    public static class ModalConstants
    {
        /// <summary>
        /// Legacy admin modals handled by UserInteractions event handler.
        /// </summary>
        public static readonly string[] LegacyModals = new[]
        {
            "joining_message",
            "parting_message",
            "discord_server_note"
        };

        /// <summary>
        /// Poll-related modals handled by UserInteractions event handler.
        /// </summary>
        public static readonly string[] PollModals = new[]
        {
            "poll_create_modal"
        };

        /// <summary>
        /// Prefix for poll vote button custom IDs.
        /// Format: poll_vote~{userId}~{pollId}~{optionId}
        /// </summary>
        public const string PollVotePrefix = "poll_vote~";

        /// <summary>
        /// Prefix for poll close button custom IDs.
        /// Format: poll_close~{creatorId}~{pollId}
        /// </summary>
        public const string PollClosePrefix = "poll_close~";
    }
}
