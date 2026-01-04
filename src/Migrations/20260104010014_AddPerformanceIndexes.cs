using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddPerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Autocomplete queries (most frequent) - used in WowCacheService.GetRioSearchHistoryAsync
            migrationBuilder.CreateIndex(
                name: "IX_RioSearchHistory_DiscordUserId_SearchCount",
                table: "RioSearchHistory",
                columns: new[] { "DiscordUserId", "SearchCount", "LastSearched" },
                descending: new[] { false, true, true });

            // Main character lookups - used in WowCacheService.GetUserMainCharacterAsync
            migrationBuilder.CreateIndex(
                name: "IX_WowCharAssociation_UserId_IsMain",
                table: "WowCharAssociation",
                columns: new[] { "UserId", "IsMain" });

            // Away mention filtering - used in AwayCommands.AwayMentionFinder
            migrationBuilder.CreateIndex(
                name: "IX_AwaySystem_UserId_Status",
                table: "AwaySystem",
                columns: new[] { "UserId", "Status" },
                filter: "\"Status\" = true");

            // Guild moderation settings - used in ModerationWatcherService.GetSettingsAsync
            migrationBuilder.CreateIndex(
                name: "IX_ModerationWatcher_DiscordGuildId",
                table: "ModerationWatcher",
                column: "DiscordGuildId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RioSearchHistory_DiscordUserId_SearchCount",
                table: "RioSearchHistory");

            migrationBuilder.DropIndex(
                name: "IX_WowCharAssociation_UserId_IsMain",
                table: "WowCharAssociation");

            migrationBuilder.DropIndex(
                name: "IX_AwaySystem_UserId_Status",
                table: "AwaySystem");

            migrationBuilder.DropIndex(
                name: "IX_ModerationWatcher_DiscordGuildId",
                table: "ModerationWatcher");
        }
    }
}
