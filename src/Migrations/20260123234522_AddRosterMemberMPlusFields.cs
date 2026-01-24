using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NinjaBotCore.Migrations
{
    /// <inheritdoc />
    public partial class AddRosterMemberMPlusFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClassId",
                table: "WowGuildRosterMembers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ItemLevel",
                table: "WowGuildRosterMembers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MythicPlusScore",
                table: "WowGuildRosterMembers",
                type: "double precision",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClassId",
                table: "WowGuildRosterMembers");

            migrationBuilder.DropColumn(
                name: "ItemLevel",
                table: "WowGuildRosterMembers");

            migrationBuilder.DropColumn(
                name: "MythicPlusScore",
                table: "WowGuildRosterMembers");
        }
    }
}
