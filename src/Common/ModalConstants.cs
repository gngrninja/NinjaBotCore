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
        /// Prefix for poll view voters button custom IDs.
        /// Format: poll_voters~{pollId}
        /// Handler: PollComponentHandlers.HandlePollViewVoters
        /// Only shown for non-anonymous polls.
        /// </summary>
        public const string PollViewVotersPrefix = "poll_voters~";

        /// <summary>
        /// Housing decor component IDs - handlers in HousingCommands.cs
        /// Standard format: housing_{action}~{userId}~{charName}~{realm}~{region}~{search}~{currentPage}[~{totalPages}]
        /// Note: Pagination buttons use unique action prefixes (_first, _prev, _next, _last) per the pattern in PATTERNS.md
        /// </summary>
        public const string HousingBrowse = "housing_browse";         // Initial browse button from summary
        public const string HousingFirst = "housing_first";           // Pagination: go to first page
        public const string HousingPrev = "housing_prev";             // Pagination: go to previous page
        public const string HousingNext = "housing_next";             // Pagination: go to next page
        public const string HousingLast = "housing_last";             // Pagination: go to last page (includes totalPages)
        public const string HousingDetails = "housing_details";       // Select menu for item details
        public const string HousingDetailBack = "housing_detail_back"; // Back button from item detail view
        public const string HousingSearch = "housing_search";         // Search button opens modal
        public const string HousingSearchModal = "housing_search_modal"; // Search modal ID
        public const string HousingClear = "housing_clear";           // Clear search filter
        public const string HousingBack = "housing_back";             // Back to summary button

        /// <summary>
        /// Classic character component IDs - handlers in CharClassicCommands.cs
        /// Standard format: charclassic_{action}~{userId}~{name}~{realm}~{region}
        /// </summary>
        public const string ClassicCharOverview = "charclassic_view_overview";
        public const string ClassicCharGear = "charclassic_view_gear";
        public const string ClassicCharRaids = "charclassic_view_raids";
        public const string ClassicCharRefresh = "charclassic_refresh";
        public const string ClassicCharShare = "charclassic_share";

        /// <summary>
        /// CraftLink component IDs - handlers in CraftComponentHandlers.cs
        /// Format: craft_{action}~{ticketId}
        /// </summary>
        public const string CraftClaimPrefix = "craft_claim~";
        public const string CraftCraftedPrefix = "craft_crafted~";
        public const string CraftCompletePrefix = "craft_complete~";
        public const string CraftCancelPrefix = "craft_cancel~";
        public const string CraftRequestModalPrefix = "craft_req~";
        public const string CraftListFilterPrefix = "craft_list_filter~";
        public const string CraftBoardFilterPrefix = "craft_board_filter~";
        public const string CraftProfessionSelectPrefix = "craft_prof_select~";
        public const string CraftJoinRolePrefix = "craft_join_role~";
        public const string CraftGotItPrefix = "craft_gotit~";
    }

    /// <summary>
    /// Shared constants for the CraftLink system.
    /// </summary>
    public static class CraftConstants
    {
        public static readonly string[] ActiveStatuses = { "Open", "Claimed", "Crafted" };

        public static readonly string[] GatheringProfessions = { "Mining", "Herbalism", "Skinning", "Fishing" };
    }
}
