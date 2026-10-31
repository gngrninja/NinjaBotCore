using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddForeverAssociations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ForeverCharacters",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    FirstName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NormalizedFirstName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NormalizedLastName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Region = table.Column<int>(type: "integer", nullable: false),
                    Ruleset = table.Column<int>(type: "integer", nullable: false),
                    Faction = table.Column<int>(type: "integer", nullable: false),
                    Class = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForeverCharacters", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ForeverGuilds",
                columns: table => new
                {
                    DiscordGuildId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Region = table.Column<int>(type: "integer", nullable: false),
                    Ruleset = table.Column<int>(type: "integer", nullable: false),
                    Faction = table.Column<int>(type: "integer", nullable: false),
                    RosterEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    SetById = table.Column<long>(type: "bigint", nullable: true),
                    SetByName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TimeSet = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForeverGuilds", x => x.DiscordGuildId);
                });

            migrationBuilder.CreateTable(
                name: "ForeverMains",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DiscordGuildId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    SetAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForeverMains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ForeverMains_ForeverCharacters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "ForeverCharacters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ForeverRosterShares",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DiscordGuildId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    CharacterId = table.Column<long>(type: "bigint", nullable: false),
                    SharedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForeverRosterShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ForeverRosterShares_ForeverCharacters_CharacterId",
                        column: x => x.CharacterId,
                        principalTable: "ForeverCharacters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ForeverCharacters_UserId_Region_Name",
                table: "ForeverCharacters",
                columns: new[] { "UserId", "Region", "NormalizedFirstName", "NormalizedLastName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForeverMains_CharacterId",
                table: "ForeverMains",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_ForeverMains_DiscordGuildId_UserId",
                table: "ForeverMains",
                columns: new[] { "DiscordGuildId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForeverRosterShares_CharacterId",
                table: "ForeverRosterShares",
                column: "CharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_ForeverRosterShares_DiscordGuildId_CharacterId",
                table: "ForeverRosterShares",
                columns: new[] { "DiscordGuildId", "CharacterId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ForeverRosterShares_DiscordGuildId_UserId",
                table: "ForeverRosterShares",
                columns: new[] { "DiscordGuildId", "UserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ForeverGuilds");

            migrationBuilder.DropTable(
                name: "ForeverMains");

            migrationBuilder.DropTable(
                name: "ForeverRosterShares");

            migrationBuilder.DropTable(
                name: "ForeverCharacters");
        }
    }
}
