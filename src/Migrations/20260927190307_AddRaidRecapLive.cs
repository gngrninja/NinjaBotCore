using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddRaidRecapLive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RaidRecapLiveCards",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DiscordGuildId = table.Column<long>(type: "bigint", nullable: false),
                    ChannelId = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<long>(type: "bigint", nullable: false),
                    ReportCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    GuildName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Region = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    ZoneName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Fingerprint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Failures = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaidRecapLiveCards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RaidRecapLiveSettings",
                columns: table => new
                {
                    DiscordGuildId = table.Column<long>(type: "bigint", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ChannelId = table.Column<long>(type: "bigint", nullable: true),
                    SetById = table.Column<long>(type: "bigint", nullable: true),
                    SetByName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TimeSet = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaidRecapLiveSettings", x => x.DiscordGuildId);
                });

            migrationBuilder.CreateTable(
                name: "RaidRecapRollout",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RaidRecapRollout", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RaidRecapLiveCards_DiscordGuildId_ReportCode",
                table: "RaidRecapLiveCards",
                columns: new[] { "DiscordGuildId", "ReportCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RaidRecapLiveCards_State",
                table: "RaidRecapLiveCards",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RaidRecapLiveCards");

            migrationBuilder.DropTable(
                name: "RaidRecapLiveSettings");

            migrationBuilder.DropTable(
                name: "RaidRecapRollout");
        }
    }
}
