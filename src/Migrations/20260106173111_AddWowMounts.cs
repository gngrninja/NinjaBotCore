using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddWowMounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WowMounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Source = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SourceDetail = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DropLocation = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    IsGround = table.Column<bool>(type: "boolean", nullable: false),
                    IsFlying = table.Column<bool>(type: "boolean", nullable: false),
                    IsAquatic = table.Column<bool>(type: "boolean", nullable: false),
                    Faction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    MediaUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WowMounts", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WowMounts");
        }
    }
}
