using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddForeverLogMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ForeverReportId",
                table: "LogMonitoring",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastCheckedForever",
                table: "LogMonitoring",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LatestLogForever",
                table: "LogMonitoring",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ForeverReportId",
                table: "LogMonitoring");

            migrationBuilder.DropColumn(
                name: "LastCheckedForever",
                table: "LogMonitoring");

            migrationBuilder.DropColumn(
                name: "LatestLogForever",
                table: "LogMonitoring");
        }
    }
}
