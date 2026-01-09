using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddAchievementsAndPets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WowAchievements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Points = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CategoryId = table.Column<long>(type: "bigint", nullable: true),
                    ParentCategory = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsAccountWide = table.Column<bool>(type: "boolean", nullable: false),
                    RewardDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RewardItemId = table.Column<long>(type: "bigint", nullable: true),
                    RewardMountId = table.Column<long>(type: "bigint", nullable: true),
                    RewardTitle = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Faction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    MediaUrl = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowAchievements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WowPets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    SpeciesId = table.Column<long>(type: "bigint", nullable: true),
                    CreatureId = table.Column<long>(type: "bigint", nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    PetType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SourceDetail = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SourceZone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IsCapturable = table.Column<bool>(type: "boolean", nullable: false),
                    IsTradable = table.Column<bool>(type: "boolean", nullable: false),
                    IsBattlePet = table.Column<bool>(type: "boolean", nullable: false),
                    Faction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    MediaUrl = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    IconUrl = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowPets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WowAchievementCriteria",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    AchievementId = table.Column<long>(type: "bigint", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OrderIndex = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    IsCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowAchievementCriteria", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WowAchievementCriteria_WowAchievements_AchievementId",
                        column: x => x.AchievementId,
                        principalTable: "WowAchievements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WowAchievementCriteria_AchievementId",
                table: "WowAchievementCriteria",
                column: "AchievementId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WowAchievementCriteria");

            migrationBuilder.DropTable(
                name: "WowPets");

            migrationBuilder.DropTable(
                name: "WowAchievements");
        }
    }
}
