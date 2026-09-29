using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class SplitRaidRecapLiveCardsByRaid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RaidRecapLiveCards_DiscordGuildId_ReportCode",
                table: "RaidRecapLiveCards");

            migrationBuilder.AddColumn<long>(
                name: "SessionStartMs",
                table: "RaidRecapLiveCards",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ZoneId",
                table: "RaidRecapLiveCards",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RaidRecapLiveCards_DiscordGuildId_ReportCode_SessionStartMs",
                table: "RaidRecapLiveCards",
                columns: new[] { "DiscordGuildId", "ReportCode", "SessionStartMs" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RaidRecapLiveCards_DiscordGuildId_ReportCode_SessionStartMs",
                table: "RaidRecapLiveCards");

            migrationBuilder.DropColumn(
                name: "SessionStartMs",
                table: "RaidRecapLiveCards");

            migrationBuilder.DropColumn(
                name: "ZoneId",
                table: "RaidRecapLiveCards");

            migrationBuilder.CreateIndex(
                name: "IX_RaidRecapLiveCards_DiscordGuildId_ReportCode",
                table: "RaidRecapLiveCards",
                columns: new[] { "DiscordGuildId", "ReportCode" },
                unique: true);
        }
    }
}
