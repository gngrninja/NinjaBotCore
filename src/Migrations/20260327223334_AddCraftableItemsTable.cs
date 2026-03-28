using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddCraftableItemsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CraftableItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    RecipeName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CraftedItemName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CraftedItemId = table.Column<long>(type: "bigint", nullable: true),
                    Profession = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProfessionId = table.Column<long>(type: "bigint", nullable: false),
                    SkillTier = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CraftableItems", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CraftableItems");
        }
    }
}
