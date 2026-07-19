using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddPushGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PushGroups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GuildId = table.Column<long>(type: "bigint", nullable: false),
                    ChannelId = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<long>(type: "bigint", nullable: false),
                    FollowupMessageId = table.Column<long>(type: "bigint", nullable: true),
                    CreatorUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatorUserName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DungeonSlug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DungeonName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TargetKeyLevel = table.Column<int>(type: "integer", nullable: false),
                    IoRatingTarget = table.Column<decimal>(type: "numeric", nullable: true),
                    IoRatingMin = table.Column<decimal>(type: "numeric", nullable: true),
                    IoRatingMax = table.Column<decimal>(type: "numeric", nullable: true),
                    KeyHolderUserId = table.Column<long>(type: "bigint", nullable: true),
                    KeyHolderDungeonName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    KeyHolderKeyLevel = table.Column<int>(type: "integer", nullable: true),
                    ScheduledForUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Region = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerPushGroupSettings",
                columns: table => new
                {
                    DiscordGuildId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MaxOpenGroups = table.Column<int>(type: "integer", nullable: true),
                    DefaultChannelId = table.Column<long>(type: "bigint", nullable: true),
                    DefaultIoWindow = table.Column<int>(type: "integer", nullable: false),
                    SetById = table.Column<long>(type: "bigint", nullable: true),
                    SetByName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TimeSet = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerPushGroupSettings", x => x.DiscordGuildId);
                });

            migrationBuilder.CreateTable(
                name: "UserPushGroupSettings",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DmOnGroupFull = table.Column<bool>(type: "boolean", nullable: false),
                    DmOnRosterPing = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPushGroupSettings", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "WeeklyKeyHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    WowCharacterName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    WowCharacterRealm = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DungeonSlug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    WeekStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BestKeyLevel = table.Column<int>(type: "integer", nullable: false),
                    LastRefreshedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyKeyHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PushGroupSignups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PushGroupId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    UserName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RoleSlot = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    SlotIndex = table.Column<int>(type: "integer", nullable: false),
                    WowCharacterName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    WowCharacterRealm = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    WowClass = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    WowSpec = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IoRating = table.Column<decimal>(type: "numeric", nullable: true),
                    IoBestThisWeek = table.Column<int>(type: "integer", nullable: true),
                    SignedUpAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    WithdrewAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushGroupSignups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PushGroupSignups_PushGroups_PushGroupId",
                        column: x => x.PushGroupId,
                        principalTable: "PushGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PushGroups_GuildId_Status",
                table: "PushGroups",
                columns: new[] { "GuildId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PushGroups_Status_ScheduledForUtc",
                table: "PushGroups",
                columns: new[] { "Status", "ScheduledForUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PushGroupSignups_PushGroupId_UserId",
                table: "PushGroupSignups",
                columns: new[] { "PushGroupId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyKeyHistory_UserId_WeekStartUtc_DungeonSlug",
                table: "WeeklyKeyHistory",
                columns: new[] { "UserId", "WeekStartUtc", "DungeonSlug" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PushGroupSignups");

            migrationBuilder.DropTable(
                name: "ServerPushGroupSettings");

            migrationBuilder.DropTable(
                name: "UserPushGroupSettings");

            migrationBuilder.DropTable(
                name: "WeeklyKeyHistory");

            migrationBuilder.DropTable(
                name: "PushGroups");
        }
    }
}
