using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddHousingDecor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HousingDecor",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    LinkedItemId = table.Column<long>(type: "bigint", nullable: true),
                    IconUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HousingDecor", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HousingDecor");
        }
    }
}
