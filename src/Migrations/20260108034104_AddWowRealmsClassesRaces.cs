using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddWowRealmsClassesRaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WowPlayableClasses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PowerType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    MediaUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowPlayableClasses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WowRaces",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Faction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    IsPlayable = table.Column<bool>(type: "boolean", nullable: false),
                    IsAlliedRace = table.Column<bool>(type: "boolean", nullable: false),
                    MediaUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowRaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WowRealms",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Region = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Timezone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Population = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ConnectedRealmId = table.Column<long>(type: "bigint", nullable: true),
                    Locale = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    IsTournament = table.Column<bool>(type: "boolean", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowRealms", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WowPlayableClasses");

            migrationBuilder.DropTable(
                name: "WowRaces");

            migrationBuilder.DropTable(
                name: "WowRealms");
        }
    }
}
