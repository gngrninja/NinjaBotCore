namespace NinjaBotCore.Common
{
    /// <summary>
    /// Constants for modal and component custom IDs used across the bot.
    /// These provide a single source of truth for custom IDs used in modals and components.
    /// </summary>
    public static class ModalConstants
    {
        /// <summary>
        /// Admin modal IDs - handlers in DiscordHelpers.cs
        /// </summary>
        public static readonly string[] LegacyModals = new[]
        {
            "joining_message",
            "parting_message",
            "discord_server_note"
        };

        /// <summary>
        /// Poll modal IDs - handlers in PollComponentHandlers.cs
        /// </summary>
        public static readonly string[] PollModals = new[]
        {
            "poll_create_modal"
        };

        /// <summary>
        /// Prefix for poll vote button custom IDs.
        /// Format: poll_vote~{userId}~{pollId}~{optionId}
        /// Handler: PollComponentHandlers.HandlePollVote
        /// </summary>
        public const string PollVotePrefix = "poll_vote~";

        /// <summary>
        /// Prefix for poll close button custom IDs.
        /// Format: poll_close~{creatorId}~{pollId}
        /// Handler: PollComponentHandlers.HandlePollClose
        /// </summary>
        public const string PollClosePrefix = "poll_close~";

        /// <summary>
        /// Housing decor component IDs - handlers in HousingCommands.cs
        /// Format: housing_{action}~{userId}~{charName}~{realm}~{region}~{page}~{search}
        /// </summary>
        public const string HousingBrowse = "housing_browse";
        public const string HousingPage = "housing_page";
        public const string HousingSearch = "housing_search";
        public const string HousingSearchModal = "housing_search_modal";
        public const string HousingBack = "housing_back";
        public const string HousingClearSearch = "housing_clear";
    }
}
