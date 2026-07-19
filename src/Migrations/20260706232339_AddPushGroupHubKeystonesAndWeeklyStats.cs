using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddPushGroupHubKeystonesAndWeeklyStats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RunCount",
                table: "WeeklyKeyHistory",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "HubChannelId",
                table: "ServerPushGroupSettings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "HubMessageId",
                table: "ServerPushGroupSettings",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderSentAt",
                table: "PushGroups",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserKeystones",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CharacterName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DungeonSlug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DungeonName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    KeyLevel = table.Column<int>(type: "integer", nullable: false),
                    WeekStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserKeystones", x => x.UserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PushGroupSignups_ActiveSlot",
                table: "PushGroupSignups",
                columns: new[] { "PushGroupId", "RoleSlot", "SlotIndex" },
                unique: true,
                filter: "\"WithdrewAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserKeystones");

            migrationBuilder.DropIndex(
                name: "IX_PushGroupSignups_ActiveSlot",
                table: "PushGroupSignups");

            migrationBuilder.DropColumn(
                name: "RunCount",
                table: "WeeklyKeyHistory");

            migrationBuilder.DropColumn(
                name: "HubChannelId",
                table: "ServerPushGroupSettings");

            migrationBuilder.DropColumn(
                name: "HubMessageId",
                table: "ServerPushGroupSettings");

            migrationBuilder.DropColumn(
                name: "ReminderSentAt",
                table: "PushGroups");
        }
    }
}
