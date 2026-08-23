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
        /// Retail character insight component IDs - handlers in CharCommands.cs.
        /// </summary>
        public const string CharInsightsSelect = "char_insights";
        public const string CharRivalsScopeSelect = "char_rivals_scope";
        public const string CharRunReviewSelect = "char_run_review";

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

        /// <summary>
        /// PushGroup component/modal IDs - handlers in PushGroupComponentHandlers.cs
        /// Wizard custom IDs are keyed by the wizard's owner user id.
        /// Live-post custom IDs are keyed by the PushGroup row id.
        /// </summary>
        // Wizard (ephemeral, edited in-place)
        public const string PushGroupWizardDungeonPrefix = "pushgroup_wiz_dungeon~";    // pushgroup_wiz_dungeon~{userId}
        public const string PushGroupWizardKeyPrefix = "pushgroup_wiz_key~";            // pushgroup_wiz_key~{userId}~{level}
        public const string PushGroupWizardKeyModalPrefix = "pushgroup_wiz_keymodal~";  // pushgroup_wiz_keymodal~{userId} (custom-level modal)
        public const string PushGroupWizardRolePrefix = "pushgroup_wiz_role~";          // pushgroup_wiz_role~{userId}~{role}
        public const string PushGroupWizardCharPrefix = "pushgroup_wiz_char~";          // pushgroup_wiz_char~{userId}
        public const string PushGroupWizardTimeModalPrefix = "pushgroup_wiz_timemodal~";// pushgroup_wiz_timemodal~{userId}
        public const string PushGroupWizardSkipTimePrefix = "pushgroup_wiz_skiptime~";  // pushgroup_wiz_skiptime~{userId}
        public const string PushGroupWizardPostPrefix = "pushgroup_wiz_post~";          // pushgroup_wiz_post~{userId}
        public const string PushGroupWizardCancelPrefix = "pushgroup_wiz_cancel~";      // pushgroup_wiz_cancel~{userId}
        public const string PushGroupWizardBackPrefix = "pushgroup_wiz_back~";          // pushgroup_wiz_back~{userId}~{step}

        // Live post buttons (keyed by PushGroup id)
        public const string PushGroupSignupPrefix = "pushgroup_signup~";       // pushgroup_signup~{groupId}~{role}
        public const string PushGroupWithdrawPrefix = "pushgroup_withdraw~";   // pushgroup_withdraw~{groupId}
        public const string PushGroupBringKeyPrefix = "pushgroup_bringkey~";   // pushgroup_bringkey~{groupId}~{targetKeyLevel}
        public const string PushGroupBringKeyModalPrefix = "pushgroup_bringkeymodal~"; // pushgroup_bringkeymodal~{groupId}
        public const string PushGroupClosePrefix = "pushgroup_close~";         // pushgroup_close~{groupId}
        public const string PushGroupRerunPrefix = "pushgroup_rerun~";         // pushgroup_rerun~{groupId} ("run it back" on closed cards)
        public const string PushGroupKeyGoPrefix = "pushgroup_keygo~";         // pushgroup_keygo~{userId}~{role} (post group from registered key)
        public const string PushGroupHubNewId = "pushgroup_hubnew";            // fixed id on the hub card's ➕ button
        public const string PushGroupHubInsightsId = "pushgroup_hubinsights";  // fixed id: open caller's main character Insights privately
    }

    /// <summary>
    /// Shared constants for the CraftLink system.
    /// </summary>
    public static class CraftConstants
    {
        public static readonly string[] ActiveStatuses = { "Open", "Claimed", "Crafted" };

        public static readonly string[] GatheringProfessions = { "Mining", "Herbalism", "Skinning", "Fishing" };
    }

    /// <summary>
    /// Shared constants for the /keys system.
    /// </summary>
    public static class PushGroupConstants
    {
        public const string StatusOpen = "Open";
        public const string StatusFull = "Full";
        public const string StatusInProgress = "InProgress";
        public const string StatusCompleted = "Completed";
        public const string StatusCancelled = "Cancelled";

        public const string RoleTank = "Tank";
        public const string RoleHealer = "Healer";
        public const string RoleDps = "DPS";

        public const int DefaultDpsSlots = 3;
        public const int DefaultIoWindow = 200;
        public const int WizardTtlMinutes = 5;

        /// <summary>
        /// WeeklyKeyHistory sentinel slug for "character refreshed, zero runs this week" —
        /// keeps freshness tracking working without fake dungeons; exclude via RunCount > 0.
        /// </summary>
        public const string NoRunsSentinelSlug = "__none__";
    }
}
