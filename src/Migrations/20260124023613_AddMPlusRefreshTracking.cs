using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddMPlusRefreshTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastMPlusRefresh",
                table: "WowGuildAssociations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MPlusRefreshCountToday",
                table: "WowGuildAssociations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "MPlusRefreshDate",
                table: "WowGuildAssociations",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastMPlusRefresh",
                table: "WowGuildAssociations");

            migrationBuilder.DropColumn(
                name: "MPlusRefreshCountToday",
                table: "WowGuildAssociations");

            migrationBuilder.DropColumn(
                name: "MPlusRefreshDate",
                table: "WowGuildAssociations");
        }
    }
}
