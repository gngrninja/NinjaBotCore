using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddLastCheckedColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastCheckedClassic",
                table: "LogMonitoring",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCheckedRetail",
                table: "LogMonitoring",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCheckedVanilla",
                table: "LogMonitoring",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastCheckedClassic",
                table: "LogMonitoring");

            migrationBuilder.DropColumn(
                name: "LastCheckedRetail",
                table: "LogMonitoring");

            migrationBuilder.DropColumn(
                name: "LastCheckedVanilla",
                table: "LogMonitoring");
        }
    }
}
