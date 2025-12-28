using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// This migration removes fields added in WclActivityTracking (20251225070749).
    /// Net effect: No schema changes from the two migrations combined.
    /// These migrations are kept for databases that already applied them.
    /// </remarks>
    public partial class RemoveUnusedWclActivityFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActivityTier",
                table: "WowGuildAssociations");

            migrationBuilder.DropColumn(
                name: "ConsecutiveNoReports",
                table: "WowGuildAssociations");

            migrationBuilder.DropColumn(
                name: "LastWclCheckDate",
                table: "WowGuildAssociations");

            migrationBuilder.DropColumn(
                name: "LastWclReportDate",
                table: "WowGuildAssociations");

            migrationBuilder.DropColumn(
                name: "WclGuildExists",
                table: "WowGuildAssociations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ActivityTier",
                table: "WowGuildAssociations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveNoReports",
                table: "WowGuildAssociations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastWclCheckDate",
                table: "WowGuildAssociations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastWclReportDate",
                table: "WowGuildAssociations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WclGuildExists",
                table: "WowGuildAssociations",
                type: "boolean",
                nullable: true);
        }
    }
}
